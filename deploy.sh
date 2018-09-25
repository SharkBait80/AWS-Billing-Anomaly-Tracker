#!/usr/bin/env bash

sam package --profile <your profile> --template-file template.yml --s3-bucket <your bucket> --output-template-file packaged.yml

aws cloudformation deploy --template-file packaged.yml --stack-name Billing-Anomaly-Tracker  --capabilities CAPABILITY_IAM --profile <your profile> --region ap-southeast-2