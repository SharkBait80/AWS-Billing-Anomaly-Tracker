using System;
using System.Collections.Generic;
using System.Text;

namespace SharedObjects
{
    public class Constants
    {
        public const string FinalizerJobId = "5eed42e2-03ad-4106-af13-1b8414a3e76c";

       public const string UsageTypesSSMPath = "/Billing/BAT/UsageTypes";

        public const string DaysOfWeekSSMPath = "/Billing/BAT/DaysOfWeek";

        public const string LookBackPeriodSSMPath = "/Billing/BAT/LookBackPeriod";

        public const string ChangeThresholdSSMPath = "/Billing/BAT/ChangeThreshold";

        public const string SnsTopicArnSSMPath= "/Billing/BAT/SNSTopicARN";

        public const string LinkedAccountsSSMPath = "/Billing/BAT/LinkedAccounts";

        public const string MinIncreaseThresholdSSMPath = "/Billing/BAT/MinIncreaseThreshold";
    }
}
