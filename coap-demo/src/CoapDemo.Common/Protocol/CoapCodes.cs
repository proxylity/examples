namespace CoapDemo.Common.Protocol;

/// <summary>CoAP method codes carried in the request <c>Code</c> field (RFC 7252 §12.1.1).</summary>
public static class CoapMethods
{
    public const int Get = 1;
    public const int Post = 2;
    public const int Put = 3;
    public const int Delete = 4;
}

/// <summary>CoAP message types carried in the <c>Type</c> field (RFC 7252 §3).</summary>
public static class CoapTypes
{
    public const int Confirmable = 0;
    public const int NonConfirmable = 1;
    public const int Acknowledgement = 2;
    public const int Reset = 3;
}

/// <summary>
/// CoAP response codes (RFC 7252 §12.1.2, §5.9) used by this demo, expressed in the
/// dotted-string form the Proxylity CoAP formatter expects (e.g. "2.05").
/// </summary>
public static class CoapCodes
{
    /// <summary>RFC 7252 §4.1 Empty message. MUST carry a zero-length Token and no Options or Payload (message is exactly 4 bytes on the wire).</summary>
    public const string Empty = "0.00";
    public const string Created = "2.01";
    public const string Deleted = "2.02";
    public const string Valid = "2.03";
    public const string Changed = "2.04";
    public const string Content = "2.05";
    public const string Continue = "2.31";
    public const string BadRequest = "4.00";
    public const string Unauthorized = "4.01";
    public const string BadOption = "4.02";
    public const string Forbidden = "4.03";
    public const string NotFound = "4.04";
    public const string MethodNotAllowed = "4.05";
    public const string NotAcceptable = "4.06";
    public const string RequestEntityIncomplete = "4.08";
    public const string RequestEntityTooLarge = "4.13";
    public const string UnsupportedContentFormat = "4.15";
    public const string InternalServerError = "5.00";
}
