#!/usr/bin/env python3
import os
import boto3
from datetime import datetime

QUEUE_URL = os.environ.get('MAVLINK_FIFO_QUEUE_URL')
if not QUEUE_URL:
    print("Error: MAVLINK_FIFO_QUEUE_URL environment variable not set")
    exit(1)

sqs = boto3.client('sqs', region_name='us-west-2')

print(f"Polling queue: {QUEUE_URL}")
print("Press Ctrl+C to exit\n")

while True:
    try:
        response = sqs.receive_message(
            QueueUrl=QUEUE_URL,
            MaxNumberOfMessages=1,
            WaitTimeSeconds=20,
            AttributeNames=['All'],
            MessageAttributeNames=['All']
        )
        
        if 'Messages' in response:
            for msg in response['Messages']:
                print(f"[{datetime.now().strftime('%H:%M:%S')}] {msg}\n")
                
                sqs.delete_message(
                    QueueUrl=QUEUE_URL,
                    ReceiptHandle=msg['ReceiptHandle']
                )
    
    except KeyboardInterrupt:
        print("\nExiting...")
        break
    except Exception as e:
        print(f"Error: {e}")
