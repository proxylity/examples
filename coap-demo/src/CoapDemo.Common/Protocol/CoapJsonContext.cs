using System.Text.Json.Serialization;
using CoapDemo.Common.Data;

namespace CoapDemo.Common.Protocol;

/// <summary>
/// AOT-safe System.Text.Json source-generation context shared by every Lambda in this
/// example. Extend with additional <see cref="JsonSerializableAttribute"/> declarations
/// rather than switching any project back to reflection-based serialization.
/// </summary>
[JsonSerializable(typeof(CoapOption))]
[JsonSerializable(typeof(List<CoapOption>))]
[JsonSerializable(typeof(CoapRequest))]
[JsonSerializable(typeof(CoapResponse))]
[JsonSerializable(typeof(ProxylityRemote))]
[JsonSerializable(typeof(RequestPacket))]
[JsonSerializable(typeof(ProxylityLambdaRequest))]
[JsonSerializable(typeof(ResponsePacket))]
[JsonSerializable(typeof(ProxylityLambdaResponse))]
[JsonSerializable(typeof(OutboundRemote))]
[JsonSerializable(typeof(OutboundMessage))]
[JsonSerializable(typeof(OutboundEnvelope))]
[JsonSerializable(typeof(ContactMessage))]
[JsonSerializable(typeof(AsyncResponseSchedule))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, long>))]
public partial class CoapJsonContext : JsonSerializerContext { }
