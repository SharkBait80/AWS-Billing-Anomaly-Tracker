using Amazon.SimpleSystemsManagement;
using System;
using System.Collections.Generic;
using System.Text;

namespace SharedObjects
{
    public class SSMParameterManager
    {
        static Amazon.SimpleSystemsManagement.AmazonSimpleSystemsManagementClient simpleSystemsManagementClient = new Amazon.SimpleSystemsManagement.AmazonSimpleSystemsManagementClient(Amazon.RegionEndpoint.APSoutheast2);

        public bool TryGetValue(string ParameterPath, out string ParameterValue)
        {
            try
            {
                var ssmParamResult = simpleSystemsManagementClient.GetParameterAsync(new Amazon.SimpleSystemsManagement.Model.GetParameterRequest
                {
                    Name = ParameterPath
                }).GetAwaiter().GetResult();

                if (ssmParamResult.HttpStatusCode == System.Net.HttpStatusCode.OK)
                {
                    ParameterValue = ssmParamResult.Parameter.Value;
                    return true;
                }
                else
                {
                    ParameterValue = null;
                    return false;
                }
            }catch (AmazonSimpleSystemsManagementException ex)
            {
                Console.WriteLine($"WARN: Error trying to retrieve SSM parameter {ParameterPath}: {ex.Message}");
                ParameterValue = null;
                return false;
            }
        }
    }
}
