// C# lambda function that updates the ASN data in DynamoDB: 
// - download the IP to ASN mapping data
// - query DDB for any blocked ASNs
// - filter the ASN data to only include the blocked ASNs
// - serialize the ASN data to a binary format
// - write the updated ASN data to DDB

