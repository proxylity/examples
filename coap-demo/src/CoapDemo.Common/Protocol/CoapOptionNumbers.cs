namespace CoapDemo.Common.Protocol;

/// <summary>
/// CoAP option numbers used by this demo (RFC 7252 §12.2, RFC 7641 §2, RFC 7959 §2.1).
/// </summary>
public static class CoapOptionNumbers
{
    public const int IfMatch = 1;
    public const int UriHost = 3;
    public const int ETag = 4;
    public const int IfNoneMatch = 5;
    public const int Observe = 6;
    public const int UriPort = 7;
    public const int LocationPath = 8;
    public const int UriPath = 11;
    public const int ContentFormat = 12;
    public const int MaxAge = 14;
    public const int UriQuery = 15;
    public const int Accept = 17;
    public const int LocationQuery = 20;
    public const int Block2 = 23;
    public const int Block1 = 27;
    public const int Size2 = 28;
    public const int ProxyUri = 35;
    public const int ProxyScheme = 39;
    public const int Size1 = 60;
}
