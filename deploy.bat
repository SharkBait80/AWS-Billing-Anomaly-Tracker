sam package --profile isengard --template-file template.yml --s3-bucket billing-anomaly-tracker-builds --output-template-file packaged.yml

aws cloudformation deploy --template-file packaged.yml --stack-name Billing-Anomaly-Tracker  --capabilities CAPABILITY_IAM --profile isengard --region ap-southeast-2