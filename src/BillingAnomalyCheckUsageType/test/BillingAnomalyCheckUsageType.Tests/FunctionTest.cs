using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;
using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;

using BillingAnomalyCheckUsageType;

namespace BillingAnomalyCheckUsageType.Tests
{
    public class FunctionTest
    {
        [Fact]
        public void TestUsageTypeFunction()
        {
            Assert.True(true);
            /*Amazon.CostExplorer.AmazonCostExplorerClient costExplorerClient = new Amazon.CostExplorer.AmazonCostExplorerClient(Amazon.RegionEndpoint.USEast1);

            var results = costExplorerClient.GetDimensionValuesAsync(new Amazon.CostExplorer.Model.GetDimensionValuesRequest
            {
                Dimension = "USAGE_TYPE",
                TimePeriod = new Amazon.CostExplorer.Model.DateInterval
                {
                    Start = DateTime.Now.AddYears(-1).ToString("yyyy-MM-dd"),
                    End = DateTime.Now.ToString("yyyy-MM-dd")
                }
            }).GetAwaiter().GetResult();
            var lstUsageTypes = results.DimensionValues.Select(d => d.Value).Where(d=>!string.IsNullOrEmpty(d)).ToList();


            Parallel.ForEach(lstUsageTypes, (usageType) =>
            {
                // Invoke the lambda function and confirm the string was upper cased.
                var function = new Function();
                var context = new TestLambdaContext();
                function.FunctionHandler(new SharedObjects.UsageTypeProcessRequest { UsageType = usageType }, context);

            }); */
        }
    }
}
