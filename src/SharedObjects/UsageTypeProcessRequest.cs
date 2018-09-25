using System;
using System.Collections.Generic;
using System.Text;

namespace SharedObjects
{
    public class UsageTypeProcessRequest
    {
        public UsageTypeProcessRequest()
        {

        }

        public string JobId
        {
            get;
            set;
        }

        public string UsageType { get; set; }

       
    }
}
