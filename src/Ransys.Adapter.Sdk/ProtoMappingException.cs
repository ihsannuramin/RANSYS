namespace Ransys.Adapter.Sdk;

/// <summary>
/// A message could not be mapped between the Protobuf wire contract and the C# contract without losing or
/// inventing information (malformed money, missing required field, unknown enum value, unsafe number, ...).
/// </summary>
public sealed class ProtoMappingException : Exception
{
    public ProtoMappingException()
    {
    }

    public ProtoMappingException(string message)
        : base(message)
    {
    }

    public ProtoMappingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
