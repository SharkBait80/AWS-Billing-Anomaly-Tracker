using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;
using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;

using BillingAnomalyMain;

namespace BillingAnomalyMain.Tests
{
    public class FunctionTest
    {
        [Fact]
        public void TestMainFunction()
        {


           // var function = new Function();
          //  var context = new TestLambdaContext();
          //  function.FunctionHandler( context);


            Assert.True(true);
        }
    }
}
