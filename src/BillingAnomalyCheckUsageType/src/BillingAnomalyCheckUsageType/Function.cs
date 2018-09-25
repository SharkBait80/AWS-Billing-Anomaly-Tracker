using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.CostExplorer;
using Amazon.CostExplorer.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda;
using Amazon.Lambda.Core; 
using Amazon.Lambda.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.XRay.Recorder.Core;
using Newtonsoft.Json;
using SharedObjects;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace BillingAnomalyCheckUsageType
{
    public class Function
    {


        private static readonly Lazy<AmazonCostExplorerClient> costExplorerClient = new Lazy<AmazonCostExplorerClient>(() =>
        {
            var ceClient = new Amazon.CostExplorer.AmazonCostExplorerClient(Amazon.RegionEndpoint.USEast1);
            return ceClient;
        });


        private static readonly Lazy<Amazon.SQS.AmazonSQSClient> sqsClient = new Lazy<AmazonSQSClient>(() => {
            var sqsClient = new AmazonSQSClient(Amazon.RegionEndpoint.APSoutheast2);
            return sqsClient;
        });

        private static readonly Lazy<AmazonDynamoDBClient> dynamoDBClient = new Lazy<AmazonDynamoDBClient>(() =>
        {
            var client = new AmazonDynamoDBClient(RegionEndpoint.APSoutheast2);
            return client;
        });

        // Used to cache the days of the week from SSM PS
        int[] DaysOfWeek = null;

       
        public void FunctionHandler(Stream inputStream, ILambdaContext context)
        {
            Amazon.XRay.Recorder.Handlers.AwsSdk.AWSSDKHandler.RegisterXRayForAllServices();

            TextReader textReader = new StreamReader(inputStream);

            var strInput = textReader.ReadToEnd();

            LambdaLogger.Log($"Received input {strInput}");

            var message = JsonConvert.DeserializeObject<SqsEvents>(strInput);

            // Could have multiple records in the message from SQS
            if (message.Records == null || message.Records.Length <= 0)
                return;

            List<string> lstJobIds = new List<string>();

            Parallel.ForEach(message.Records, (record) =>
            {

                var processRequest = JsonConvert.DeserializeObject<UsageTypeProcessRequest>(Regex.Unescape(record.body));

                LambdaLogger.Log($"Received request to process usage type {processRequest.UsageType }");

                if (!lstJobIds.Contains(processRequest.JobId))
                    lstJobIds.Add(processRequest.JobId);

                int LookbackDays = GetTotalLookbackDays();

                GetCostAndUsageResponse results = null;

                bool blnRetry = false;

                int intSleepDuration = 0;

                int intMaxWaitInterval = 5000;

                int intRetries = 0;

                int intMaxRetries = 10;

                do
                {

                    try
                    {
                        results = costExplorerClient.Value.GetCostAndUsageAsync(new Amazon.CostExplorer.Model.GetCostAndUsageRequest
                        {
                            TimePeriod = new Amazon.CostExplorer.Model.DateInterval
                            {
                                End = DateTime.Now.ToString("yyyy-MM-dd"),
                                // We need to add one day because yesterday is when the actual usage 
                                // analysis is important - it should be excluded from the average calculation
                                Start = DateTime.Now.AddDays(-1 * (LookbackDays + 1)).ToString("yyyy-MM-dd")
                            },

                            Filter = BuildCostExplorerFilter(processRequest.UsageType),
                            Granularity = Amazon.CostExplorer.Granularity.DAILY,
                            Metrics = new List<string>(new string[] { "UsageQuantity", "AmortizedCost" })
                        }).GetAwaiter().GetResult();

                        blnRetry = false;

                    }
                    catch (Amazon.CostExplorer.Model.LimitExceededException) // Use exponential backoff to wait until the API is ready again
                    {
                        blnRetry = true;

                        intSleepDuration = Convert.ToInt32((long)Math.Pow(2, intRetries) * 100L);

                        Thread.Sleep(Math.Min(intMaxWaitInterval, intSleepDuration));
                    }
                    catch (Exception ex)
                    {
                        blnRetry = false;
                        LambdaLogger.Log($"An error occurred calling the Cost Explorer API for usage type {processRequest.UsageType}: {ex.Message}");
                    }
                } while (blnRetry && (intRetries++ < intMaxRetries));

                if (results == null)
                {
                    IncrementControlTable(processRequest.JobId);
                    throw new Exception("Results not retrieved - this is likely due to the Cost Explorer API timing out.");
                }

                double dblTotal = 0d;

                double dblYesterdayUsage = 0d;

                var dblAverage = 0d;

                bool blnTrigger = false;

                double dblIncreaseAmount = 0d;

                int intActualDays = 0;

                var dblMinIncreaseThreshold = GetMinIncreaseThreshold();

                DateTime dtYesterday = DateTime.Now;

                foreach (var item in results.ResultsByTime)
                {
                    var startDate = DateTime.Parse(item.TimePeriod.Start);

                    var endDate = DateTime.Parse(item.TimePeriod.End);

                    if (DaysOfWeek != null && !DaysOfWeek.Contains(((int)startDate.DayOfWeek + 1)))
                    {
                        // This day is not included in the config
                        continue;
                    }

                    // Check if it is the appropriate day of week

                    var amortizedItem = item.Total.FirstOrDefault(a => a.Key == "AmortizedCost");

                    //   Debug.WriteLine($"{processRequest.UsageType} - {startDate.ToString("d MMM yyyy")} - {amortizedItem.Key} {amortizedItem.Value.Unit}{amortizedItem.Value.Amount}");
                    var dblValue = double.Parse(amortizedItem.Value.Amount);

                    if (intActualDays == 0)
                    {
                        dblYesterdayUsage = dblValue;
                        dtYesterday = startDate;
                    }
                    else
                        dblTotal += dblValue;

                    intActualDays++;
                }


                if (intActualDays > 1) // Must minus one to cater for the previous date
                {
                    dblAverage = dblTotal / Convert.ToDouble(intActualDays - 1);

                    if (dblYesterdayUsage > dblAverage) // The usage has increased
                    {
                        string strThreshold;

                        double dblThreshold = 0.2d; // 20% increase by default

                        if (new SSMParameterManager().TryGetValue(Constants.ChangeThresholdSSMPath, out strThreshold))
                        {
                            double.TryParse(strThreshold, out dblThreshold);
                            if (dblThreshold <= 0d)
                                dblThreshold = 0.2d;
                        }

                        dblIncreaseAmount = dblYesterdayUsage - dblAverage;

                        if (IsTestMode())
                        {
                            // 50% chance of triggering

                            blnTrigger = new Random(DateTime.Now.Millisecond).NextDouble() > 0.5d;
                        }
                        else
                        {
                            if (dblIncreaseAmount > dblMinIncreaseThreshold && dblIncreaseAmount > (dblThreshold * dblAverage))
                            {
                                blnTrigger = true;
                            }
                        }

                    }

                }

                var strDDBTableName = System.Environment.GetEnvironmentVariable("BatTable");

                var ddbResult = dynamoDBClient.Value.PutItemAsync(new PutItemRequest
                {
                    Item = new Dictionary<string, AttributeValue>()
                {
                    { "id", new AttributeValue { S= processRequest.UsageType  }},
                    { "Total", new AttributeValue { N= dblTotal.ToString() }},
                        { "Processed",new AttributeValue{BOOL=true}},
                        { "Triggered",new AttributeValue{BOOL=blnTrigger }},
                    { "AverageDaily",new AttributeValue{N=dblAverage .ToString()}},
                    { "PreviousDay",new AttributeValue{N=dblYesterdayUsage.ToString()}},
                    { "IncreaseBy",new AttributeValue{N=dblIncreaseAmount.ToString()}},
                    { "YesterdayDate",new AttributeValue{S=dtYesterday.ToString("yyyy-MM-dd")}}
                },
                    TableName = strDDBTableName
                }).GetAwaiter().GetResult();

                if (ddbResult.HttpStatusCode != System.Net.HttpStatusCode.OK)
                {
                    LambdaLogger.Log($"WARN: Unsuccessful insert of DynamoDB table {strDDBTableName }");
                }

                LambdaLogger.Log($"TOTAL FOR {processRequest.UsageType} - {dblTotal.ToString("C")}");

                IncrementControlTable(processRequest.JobId);

            });


            foreach (var JobId in lstJobIds)
            {

                // Check if this is the last usage type to process, invoke the finalizer

                string strBatFinalizerQueueUrl = System.Environment.GetEnvironmentVariable("BatFinalizerQueueUrl");

                SendMessageRequest sendMessageRequest = new SendMessageRequest
                {
                    MessageBody = JobId,
                    QueueUrl = strBatFinalizerQueueUrl
                };

                LambdaLogger.Log($"Putting message on SQS queue for Job Id {JobId}");
                var sendMessageResult = sqsClient.Value.SendMessageAsync(sendMessageRequest).GetAwaiter().GetResult();


            }

        }

        bool IsTestMode()
        {
           var strTestMode= System.Environment.GetEnvironmentVariable("TestMode");

            if (!string.IsNullOrEmpty(strTestMode))
            {
                Boolean isTest;
                if (Boolean.TryParse(strTestMode, out isTest))
                    return isTest;
                else
                    return false;
            }
            else
                return false;
        }

        void IncrementControlTable(string JobId)
        {
            AWSXRayRecorder.Instance.BeginSubsegment("Increment Control Table");
            var strDDBTableName = System.Environment.GetEnvironmentVariable("ControlTable");

            var request = new UpdateItemRequest
            {
                TableName = strDDBTableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    { "id", new AttributeValue { S =JobId } }
                 },
                AttributeUpdates = new Dictionary<string, AttributeValueUpdate>()
                {
                 {
                  "Processed",
                    new AttributeValueUpdate { Action = "ADD", Value = new AttributeValue { N = "1" } }
                    },
                 },
            };

            var response =  dynamoDBClient.Value.UpdateItemAsync(request).GetAwaiter().GetResult();

            if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
            {
                LambdaLogger.Log($"WARN: Unsuccessful update of DynamoDB table {strDDBTableName }");

            }

            AWSXRayRecorder.Instance.EndSubsegment();

        }

       

        Amazon.CostExplorer.Model.Expression BuildCostExplorerFilter(string UsageType)
        {
            Amazon.CostExplorer.Model.Expression usageValuesExpression = new Amazon.CostExplorer.Model.Expression();

            var dimUsageTypeValues = new Amazon.CostExplorer.Model.DimensionValues { Key = new Amazon.CostExplorer.Dimension("USAGE_TYPE") };

            dimUsageTypeValues.Values = new List<string>();

            dimUsageTypeValues.Values.Add(UsageType);

            usageValuesExpression.Dimensions = dimUsageTypeValues;

            var linkedAccountIds = GetLinkedAccountIds();

            if (linkedAccountIds != null && linkedAccountIds.Length > 0)
            {
                // We need to filter by linked account ID
                var dimLinkedAccount = new Amazon.CostExplorer.Model.DimensionValues { Key = new Amazon.CostExplorer.Dimension("LINKED_ACCOUNT") };

                dimLinkedAccount.Values = new List<string>();

                dimLinkedAccount.Values.AddRange(linkedAccountIds);

                Amazon.CostExplorer.Model.Expression linkedAccountExpression = new Amazon.CostExplorer.Model.Expression();

                linkedAccountExpression.Dimensions = dimLinkedAccount;

                // Add a new expression to AND the two together
                var AndExpression = new Amazon.CostExplorer.Model.Expression();

                AndExpression.And = new List<Amazon.CostExplorer.Model.Expression>();

                AndExpression.And.Add(usageValuesExpression);

                AndExpression.And.Add(linkedAccountExpression);

                return AndExpression;
            }
            else
                return usageValuesExpression;
        }

        string[] GetLinkedAccountIds()
        {
            string strLinkedIds;

            if (new SSMParameterManager().TryGetValue(Constants.LinkedAccountsSSMPath, out strLinkedIds))
            {
                if (strLinkedIds == "*")
                    return null;

                return strLinkedIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }
            else
                return null;
        }

      

        /// <summary>
        /// Gets the minimum amount, in dollars, that a usage type needs to increase by before it will be included in notifications.
        /// </summary>
        /// <returns>The minimum increase threshold.</returns>
        double GetMinIncreaseThreshold()
        {
            string strMinIncreaseThreshold;
            double dblMinIncreaseThreshold = 0d;

            if (new SSMParameterManager().TryGetValue(Constants.MinIncreaseThresholdSSMPath, out strMinIncreaseThreshold))
            {
                if (double.TryParse (strMinIncreaseThreshold,out dblMinIncreaseThreshold))
                {
                    if (dblMinIncreaseThreshold < 0d)
                        dblMinIncreaseThreshold = 0d;
                }
            }

            return dblMinIncreaseThreshold;
        }

        /// <summary>
        /// Computes and returns the actual number of days to get from the Cost Explorer API.
        /// </summary>
        /// <returns></returns>
        int GetTotalLookbackDays()
        {
            int intLookbackDays = 30; // Default
            string strLookbackDays;

            if (new SSMParameterManager().TryGetValue(Constants.LookBackPeriodSSMPath, out strLookbackDays))
            {
                if (int.TryParse(strLookbackDays, out intLookbackDays))
                    if (intLookbackDays < 1)
                        intLookbackDays = 30; // Back to default

            }

            // Next we need to establish how many days of the week are actually taken into account
            // If it's only 5 out 7 days, we need to add 2 days for each week to obtain the average from the CE API

            int intNumDaysOfWeek = 7;
            string strDaysOfWeek;

            if (new SSMParameterManager().TryGetValue(Constants.DaysOfWeekSSMPath, out strDaysOfWeek))
            {
                try
                {
                    DaysOfWeek = strDaysOfWeek.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.Parse(s))
                        .Where(i => i >= 1 && i <= 7).ToArray();

                    intNumDaysOfWeek = DaysOfWeek.Length;

                }
                catch
                {
                    // Invalid data
                }

            }

            // Add the extra days to make up for the days not checked
            intLookbackDays += (intLookbackDays / 7) * (7 - intNumDaysOfWeek);

            return intLookbackDays;
        }
    }
}
