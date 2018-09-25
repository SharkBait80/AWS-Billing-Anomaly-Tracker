using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService.Model;
using Amazon.XRay.Recorder.Core;
using BillingAnomalyCheckUsageType;
using Newtonsoft.Json;
using SharedObjects;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace BillingAnomalyFinalizer
{
    public class Function
    {

        private static readonly Lazy<AmazonDynamoDBClient> dynamoDBClient = new Lazy<AmazonDynamoDBClient>(() =>
        {
            var client = new AmazonDynamoDBClient(RegionEndpoint.APSoutheast2);
            return client;
        });

        /// <summary>
        /// Function that executes when all the check usage type lambdas are done executing.
        /// </summary>
        /// <param name="input"></param>
        /// <param name="context"></param>
        /// <returns></returns>
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

            string JobId = message.Records[0].body;

            if (GetUnprocessedItems(JobId) > 0)
                return;

            var tsTimeTaken = GetTimeTaken(JobId);

            var strSnsOutput= BuildSnsTopicString(tsTimeTaken);

            if (string.IsNullOrEmpty(strSnsOutput))
            {
                LambdaLogger.Log("No SNS data to publish, exiting.");
                return;
            }

            string SNSTopicArn = GetSNSTopicARN();

            if (SNSTopicArn == null)
            {
                LambdaLogger.Log("SNS topic ARN is missing.");
                return;
            }

            Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient snsClient = new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient(Amazon.RegionEndpoint.APSoutheast2);

            var snsClientResult=snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = SNSTopicArn,
                Message = strSnsOutput,
                 Subject="AWS Billing Anomaly Tracker"
            }).GetAwaiter().GetResult();

            if (snsClientResult.HttpStatusCode == System.Net.HttpStatusCode.OK)
            {
                LambdaLogger.Log("Successfully published to SNS.");
            }
           

        }

        TimeSpan GetTimeTaken(string JobId)
        {
            var strDDBTableName = System.Environment.GetEnvironmentVariable("ControlTable");

            Table usageTypeTable = Table.LoadTable(dynamoDBClient.Value, strDDBTableName);

            var request = new QueryRequest
            {
                TableName = strDDBTableName,
                KeyConditionExpression = "id = :v_Id",
                Limit=1,
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> {
        {":v_Id", new AttributeValue {S=  JobId}}}
            };

            var response = dynamoDBClient.Value.QueryAsync(request).GetAwaiter().GetResult();

            if (response.Items.Count > 0)
            {
                var startTime = new DateTime(Convert.ToInt64(response.Items[0]["StartTime"].N));
                return DateTime.UtcNow.Subtract(startTime);
            }
            else
                return TimeSpan.Zero;
        }

        int GetUnprocessedItems(string JobId)
        {
            AWSXRayRecorder.Instance.BeginSubsegment("Get Unprocessed Items");

            var strDDBTableName = System.Environment.GetEnvironmentVariable("ControlTable");

            Table usageTypeTable = Table.LoadTable(dynamoDBClient.Value, strDDBTableName);

            var request = new QueryRequest
            {
                TableName = strDDBTableName,
                KeyConditionExpression = "id = :v_Id",
                Limit = 1,
                ConsistentRead = true,
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> {
                {":v_Id", new AttributeValue {S = JobId }}}
            };

            var response = dynamoDBClient.Value.QueryAsync(request).GetAwaiter().GetResult();

            if (response.Items.Count > 0)
            {
                var intToProcess = Convert.ToInt32(response.Items[0]["TotalToProcess"].N);
                var intProcessed = Convert.ToInt32(response.Items[0]["Processed"].N);
                AWSXRayRecorder.Instance.EndSubsegment();
                var intUnprocessed = Math.Max(intToProcess - intProcessed, 0);
                LambdaLogger.Log($"Unprocessed item count: {intUnprocessed}");
                return intUnprocessed;
            }
            else
            {
                AWSXRayRecorder.Instance.EndSubsegment();
                return 0;
            }
        }

        string BuildSnsTopicString(TimeSpan timeTaken)
        {
            AWSXRayRecorder.Instance.BeginSubsegment("Build SNS Topic String");

            StringBuilder sbText = new StringBuilder();

           

            var strDDBTableName = System.Environment.GetEnvironmentVariable("BatTable");

            Table usageTypeTable = Table.LoadTable(dynamoDBClient.Value, strDDBTableName);

            ScanFilter scanFilter = new ScanFilter();
            scanFilter.AddCondition("Processed", ScanOperator.Equal,DynamoDBBool.True);
            scanFilter.AddCondition("Triggered", ScanOperator.Equal, DynamoDBBool.True);

            Search search = usageTypeTable.Scan(scanFilter);

            List<Document> documentList = new List<Document>();


            do
            {
                documentList = search.GetRemainingAsync().GetAwaiter().GetResult();

                if (documentList.Count>0 && sbText.Length==0)
                {
                    sbText.AppendLine("Billing Anomaly Tracker"+Environment.NewLine);

                }

                foreach (var document in documentList)
                {
                    var usageType=document["id"].AsString();
                    var averageDaily = document["AverageDaily"].AsDouble();
                    var previousDay = document["PreviousDay"].AsDouble();
                    var increaseBy = document["IncreaseBy"].AsDouble();
                    var strYesterdayDate = document["YesterdayDate"].AsString();
                    var dtYesterdayDate = DateTime.ParseExact(strYesterdayDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);

                    sbText.AppendLine($"{usageType} - increase by {increaseBy.ToString("P")} - Cost for {dtYesterdayDate.ToString("d MMM yyyy")}: {previousDay.ToString("C")} - Average Daily Cost: {averageDaily.ToString("C")}");

                    foreach (var attribute in document.GetAttributeNames())
                    {

                        string stringValue = null;
                        var value = document[attribute];
                        if (value is Primitive)
                            stringValue = value.AsPrimitive().Value.ToString();
                        else if (value is PrimitiveList)
                            stringValue = string.Join(",", (from primitive
                                            in value.AsPrimitiveList().Entries
                                                            select primitive.Value).ToArray());
                        LambdaLogger.Log($"{attribute} - {stringValue}");
                    }
                }
              
            } while (!search.IsDone);

            if (sbText.Length>0)
            {
                sbText.AppendLine($"Time taken for processing: {timeTaken.ToString(@"d\.hh\:mm\:ss")}");
            }

            AWSXRayRecorder.Instance.EndSubsegment();

            return sbText.ToString();
        }

        string GetSNSTopicARN()
        {
            string SNSTopicArn;
            if (new SSMParameterManager().TryGetValue(Constants.SnsTopicArnSSMPath, out SNSTopicArn))
            {
                return SNSTopicArn;
            }
            return null;
        }
    }
}
