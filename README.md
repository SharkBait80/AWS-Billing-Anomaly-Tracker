# Billing Anomaly Tracker for AWS

### Objective

This tool is used to help you to detect spikes in AWS usage based on an average baseline established over a period of time.

If the average baseline is exceeded by a specified threshold, a SNS notification is sent out.

### Architecture

This application is built using the AWS Serverless Application Model, with Lambda functions written in C# targeting .NET Core 2.1.

The application deploys:

- 3 x Lambda functions

- 2 x SQS queues

- 1 x SNS topic

- 2 x DynamoDB tables

- 1 x IAM Role

- 7 x SSM Parameters for configuration

### EC2 Systems Manager Parameters

* Billing/BAT/Billing/ChangeThreshold: How much the cost of a usage group type needs to change before a notification is triggered

* Billing/BAT/Billing/LookBackPeriod: The number of days to look back to form a baseline. This is set at 30 by default.

* Billing/BAT/Billing/DaysOfWeek: This specifies the days of the week to include in the check. The default is 1, 2, 3, 4, 5, which is Mon - Fri. If the customer wants to check all days of the week, this should be left blank or 1, 2, 3, 4, 5,6,7

* Billing/BAT/Billing/Services: (Optional) If set, this will limit the number of services that are checked. This should be the name of the services in a comma-separated list.

* Billing/BAT/Billing/LinkedAccounts: (Optional) If set, this will limit the accounts that are checked. This should be the IDs of the accounts in a comma-separated list.

* Billing/BAT/Billing/UsageTypes: (Optional) If set, this will limit the usage types that are checked. This should be the usage types in a comma-separated list.

* Billing/BAT/Billing/SNSTopicARN: The ARN of the SNS topic to send notifications to.

### Instructions

You'll need:

1. Install .NET Core - https://www.microsoft.com/net/download

2. Install AWS CLI - https://docs.aws.amazon.com/cli/latest/userguide/installing.html

3. Install AWS Serverless Application Model - https://github.com/awslabs/serverless-application-model

4. Run build.sh - this will install the NuGet packages and use Cake to build the projects.

5. Modify the profile and S3 bucket parameters in deploy.sh. You will need a S3 bucket and AWS credential profile for SAM deployment.

6. Run deploy.sh

7. The deployment script should have deployed a SNS topic starting with 'Billing-Anomaly-Tracker-NotificationSNSTopic'. Find this SNS topic and subscribe to it using the appropriate notification methods (e.g. email, SMS).

8. The tool is deployed in test mode by default. This randomizes the results of each check and allows you to test the notifications. To disable test mode, go to the lambda function that starts with '
Billing-Anomaly-Tracker-BillingAnomalyCheckUsageTy' and look for the environment variable called 'TestMode'. Set this to false.

9. To run the tool, execute the lambda function that starts with 'Billing-Anomaly-Tracker-BillingAnomalyMainFunction'. You can schedule this to be run using a CloudWatch event for regular checks.

### Expected Output

![alt text](https://raw.githubusercontent.com/RecursiveLoop/AWS-Billing-Anomaly-Tracker/master/BAT.png "Output")