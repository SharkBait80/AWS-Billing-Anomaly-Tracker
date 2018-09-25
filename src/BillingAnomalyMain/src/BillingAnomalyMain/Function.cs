using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon;
using Amazon.CostExplorer;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda;
using Amazon.Lambda.Core;
using Amazon.Lambda.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.XRay.Recorder.Core;
using SharedObjects;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace BillingAnomalyMain
{
    public class Function
    {

        private static readonly Lazy<Amazon.Lambda.AmazonLambdaClient> lambdaClient = new Lazy<AmazonLambdaClient>(() => { 
            var lambdaClient= new Amazon.Lambda.AmazonLambdaClient(Amazon.RegionEndpoint.APSoutheast2);
            return lambdaClient;
        });

        private static readonly Lazy<Amazon.SQS.AmazonSQSClient> sqsClient = new Lazy<AmazonSQSClient>(() => {
            var sqsClient = new AmazonSQSClient(Amazon.RegionEndpoint.APSoutheast2);
            return sqsClient;
        });

        private static readonly Lazy<AmazonCostExplorerClient> costExplorerClient = new Lazy<AmazonCostExplorerClient>(() =>
        {
            var ceClient = new Amazon.CostExplorer.AmazonCostExplorerClient(Amazon.RegionEndpoint.USEast1);
            return ceClient;
        });

        private static readonly Lazy<AmazonDynamoDBClient> dynamoDBClient = new Lazy<AmazonDynamoDBClient>(() =>
        {
            var client = new AmazonDynamoDBClient( RegionEndpoint.APSoutheast2);
            return client;
        });

        //https://console.aws.amazon.com/cost-reports/data/getMatchingDimensionValues?exclusiveEndDate=2018-08-25&inclusiveStartDate=2017-08-01&orderBy=Alphabet&resultSize=1000&searchTargetDimension=UsageType&searchTerm=

        /// <summary>
        /// A simple function that takes a string and does a ToUpper
        /// </summary>
        /// <param name="input"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public int FunctionHandler(ILambdaContext context)
        {
            Amazon.XRay.Recorder.Handlers.AwsSdk.AWSSDKHandler.RegisterXRayForAllServices();
            if (!ShouldRunToday())
            {
                LambdaLogger.Log($"Skipping execution due to day of week config");
                return 0;
            }

            ClearBatTable();

            var results = costExplorerClient.Value.GetDimensionValuesAsync(new Amazon.CostExplorer.Model.GetDimensionValuesRequest
            {
                Dimension = "USAGE_TYPE",
                TimePeriod = new Amazon.CostExplorer.Model.DateInterval
                {
                    Start = DateTime.Now.AddYears(-1).ToString("yyyy-MM-dd"),
                    End = DateTime.Now.ToString("yyyy-MM-dd")
                }
            }).GetAwaiter().GetResult();

            var lstUsageTypes = results.DimensionValues.Select(d => d.Value).Where(d=>!string.IsNullOrEmpty(d)).ToList();

            var ssmUsageTypes = GetUsageTypesFromSSM();

            int intCount = 0;

            var strDDBTableName = System.Environment.GetEnvironmentVariable("BatTable");

            var strCheckUsageTypeFunctionName = System.Environment.GetEnvironmentVariable("CheckUsageTypeFunction");

            var JobId = Guid.NewGuid().ToString();

            foreach (var usageType in lstUsageTypes)
            {
                if (ssmUsageTypes != null && ssmUsageTypes.Length > 0)
                {
                    if (!ssmUsageTypes.Contains(usageType))
                    {
                        LambdaLogger.Log($"Skipping due to usage types not in configuration.");
                        continue;
                    }
                }

                var ddbResult= dynamoDBClient.Value.PutItemAsync(new PutItemRequest {  Item = new Dictionary<string, AttributeValue>()
      {
                        { "id", new AttributeValue { S= usageType }},
                        { "Total", new AttributeValue { N= "0.00" }},
                        { "Processed",new AttributeValue{BOOL=false}},
                        { "Triggered",new AttributeValue{BOOL=false}}
      },
                    TableName = strDDBTableName }).GetAwaiter().GetResult();

                if (ddbResult.HttpStatusCode != System.Net.HttpStatusCode.OK)
                {
                    LambdaLogger.Log($"WARN: Unsuccessful insert of DynamoDB table {strDDBTableName }");
                    continue; // Unsuccessful write

                }

              
                intCount++;
            }

            PopulateControlTable(JobId,intCount);

            LambdaLogger.Log($"Found {intCount} usage types to process.");

            string strBatQueueUrl = System.Environment.GetEnvironmentVariable("BatQueueUrl");

            foreach (var usageType in lstUsageTypes)
            {
                if (ssmUsageTypes != null && ssmUsageTypes.Length > 0)
                {
                    if (!ssmUsageTypes.Contains(usageType))
                    {
                        LambdaLogger.Log($"Ignoring usage type {usageType} as it is not in the list of configured usage types.");
                        continue;
                    }
                }

                UsageTypeProcessRequest requestObject = new UsageTypeProcessRequest { JobId = JobId, UsageType = usageType };

                SendMessageRequest sendMessageRequest = new SendMessageRequest
                {
                    MessageBody = Newtonsoft.Json.JsonConvert.SerializeObject(requestObject),
                    QueueUrl = strBatQueueUrl
                };

                LambdaLogger.Log($"Putting message on SQS queue for {usageType}");
                var sendMessageResult=sqsClient.Value.SendMessageAsync(sendMessageRequest).GetAwaiter().GetResult();

               /* InvokeRequest invokeRequest = new InvokeRequest
                {
                    FunctionName = strCheckUsageTypeFunctionName,
                    Payload = Newtonsoft.Json.JsonConvert.SerializeObject(requestObject),
                    InvocationType = InvocationType.Event // Async
                };

                LambdaLogger.Log($"Invoking child lambda to process {usageType}");
                lambdaClient.Value.InvokeAsync(invokeRequest); */
            }


            return intCount;

        }

        private void PopulateControlTable(string JobId,int UsageTypeCount)
        {

            AWSXRayRecorder.Instance.BeginSubsegment("Populate Control Table");
            var strDDBTableName = System.Environment.GetEnvironmentVariable("ControlTable");

            var ddbResult = dynamoDBClient.Value.PutItemAsync(new PutItemRequest
            {
                Item = new Dictionary<string, AttributeValue>()
      {
                    { "id", new AttributeValue { S=JobId }},
                    { "TotalToProcess", new AttributeValue { N=UsageTypeCount.ToString() }},
                        { "Processed",new AttributeValue{N="0"}},
                    { "StartTime",new AttributeValue{N=DateTime.UtcNow.Ticks.ToString()}},
                    { "StartTimeString",new AttributeValue{S=DateTime.UtcNow.ToString("d MMM yyyy h:mm:ss tt")}}
      },
                TableName = strDDBTableName
            }).GetAwaiter().GetResult();

            if (ddbResult.HttpStatusCode != System.Net.HttpStatusCode.OK)
            {
                LambdaLogger.Log($"WARN: Unsuccessful insert of DynamoDB table {strDDBTableName }");


            }

            AWSXRayRecorder.Instance.EndSubsegment();
        }

        private void ClearBatTable()
        {

            AWSXRayRecorder.Instance.BeginSubsegment("Clearing BAT Table");

            var strDDBTableName = System.Environment.GetEnvironmentVariable("BatTable");

            LambdaLogger.Log($"Clearing table {strDDBTableName}");

            var conditions = new List<ScanCondition>();

            ScanRequest scanRequest = new ScanRequest
            { TableName = strDDBTableName };

            ScanResponse response;

            List<string> lstIdsToDelete = new List<string>();

            do
            {
                 response = dynamoDBClient.Value.ScanAsync(scanRequest).GetAwaiter().GetResult();

                foreach (var item in response.Items)
                {
                    var id = item["id"].S;

                    lstIdsToDelete.Add(id);

                  
                }

                 
                scanRequest.ExclusiveStartKey = response.LastEvaluatedKey;

            } while (response.LastEvaluatedKey.Count != 0);


            bool keepDeleting = true;


            do
            {
                var lstReduced = lstIdsToDelete.Take(24).ToArray();
                lstIdsToDelete = lstIdsToDelete.Skip(24).ToList();
                if (lstReduced.Length > 0)
                {
                    List<WriteRequest> deleteRequests = new List<WriteRequest>();

                    foreach (var id in lstReduced)
                    {

                        deleteRequests.Add(new WriteRequest
                        {
                            DeleteRequest = new DeleteRequest
                            {
                                Key = new Dictionary<string, AttributeValue>()
                {
                   { "id", new AttributeValue { S = id } }
                }
                            }
                        });


                    }

                    Dictionary<string, List<WriteRequest>> requestItems = new Dictionary<string, List<WriteRequest>>();

                    requestItems[strDDBTableName] = deleteRequests;

                    BatchWriteItemRequest request = new BatchWriteItemRequest { RequestItems = requestItems };

                  
                    BatchWriteItemResponse result;
                    do
                    {
                        LambdaLogger.Log($"Number of delete items: {request.RequestItems[strDDBTableName].Count}");

                        // Issue request and retrieve items
                        result = dynamoDBClient.Value.BatchWriteItemAsync(request).GetAwaiter().GetResult();

                        // Some items may not have been processed!
                        //  Set RequestItems to the result's UnprocessedItems and reissue request
                        request.RequestItems = result.UnprocessedItems;

                    } while (result.UnprocessedItems.Count > 0);
                }

                keepDeleting = lstIdsToDelete.Count > 0;
            } while (keepDeleting);

            AWSXRayRecorder.Instance.EndSubsegment();
        }

        private string[] GetUsageTypesFromSSM()
        {
            AWSXRayRecorder.Instance.BeginSubsegment("Get Usage Types from SSM");

            string ssmParamResult;

            if (new SSMParameterManager().TryGetValue(Constants.UsageTypesSSMPath, out ssmParamResult))
            {
                if (!string.IsNullOrEmpty(ssmParamResult))
                {
                    if (ssmParamResult == "*")
                        return null;

                    try
                    {
                        var usageTypes = ssmParamResult.Split(',', StringSplitOptions.RemoveEmptyEntries).ToArray();

                        if (usageTypes.Length == 0)
                            return null;

                        return usageTypes;
                    }
                    catch (Exception ex)
                    {
                        LambdaLogger.Log($"Error parsing SSM day of week parameter: {ex.Message}");
                    }
                }
                AWSXRayRecorder.Instance.EndSegment();

                return null;

            }
            else // Not specified in SSM parameter store
            {
                AWSXRayRecorder.Instance.EndSegment();
                return null;
            }
           
        }

        /// <summary>
        /// Checks SSM parameter store to see if the check should be run today.
        /// </summary>
        /// <returns><c>true</c>, if run today was shoulded, <c>false</c> otherwise.</returns>
        private bool ShouldRunToday()
        {

            string ssmParamResult;

            if (new SSMParameterManager().TryGetValue(Constants.DaysOfWeekSSMPath, out ssmParamResult))
            {
                if (!string.IsNullOrEmpty(ssmParamResult))
                {
                    try
                    {
                        var daysOfWeek = ssmParamResult.Split(',').Select(s => int.Parse(s)).ToArray();
                        var currentDayOfWeek = (int)DateTime.Now.DayOfWeek + 1;
                        return daysOfWeek.Contains(currentDayOfWeek);
                    }
                    catch (Exception ex)
                    {
                        LambdaLogger.Log($"Error parsing SSM day of week parameter: {ex.Message}");
                    }
                }

                return true;

            }
            else // Not specified in SSM parameter store
                return true;

        }
    }
}
