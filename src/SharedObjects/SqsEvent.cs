using System;
using System.Runtime.Serialization;

namespace BillingAnomalyCheckUsageType
{
    [DataContract]
    public class SqsEvents
    {
        [DataMember]
        public SqsEvent[] Records { get; set; }
    }

    [DataContract]
    public class SqsEvent
    {
        [DataMember]
        public string messageId
        {
            get;
            set;
        }

        [DataMember]
        public string md5OfBody
        {
            get;
            set;

        }

        [DataMember]
        public string eventSource
        {
            get;
            set;
        }

        [DataMember]
        public string eventSourceARN
        {
            get;
            set;
        }

        [DataMember]
        public string awsRegion
        {
            get;
            set;
        }

        [DataMember]
        public string body
        {
            get;
            set;
        }

        [DataMember]
        public SqsEventAttribute attributes
        {
            get;
            set;
        }
    }

    [DataContract]
    public class SqsEventAttribute
    {
        [DataMember]
        public int ApproximateReceiveCount
        {
            get;
            set;
        }

        [DataMember]
        public long SentTimestamp
        {
            get;
            set;
        }

        [DataMember]
        public string SenderId
        {
            get;
            set;
        }

        [DataMember]
        public long ApproximateFirstReceiveTimestamp
        {
            get;
            set;
        }


    }

}
