using System.Net;

namespace LetterboxdSync.Serializd;

/// <summary>Serializd rejected the account's credentials (401/403 on login), as opposed to a network or server error.</summary>
public class SerializdAuthException : SerializdRequestException
{
    public SerializdAuthException(string message, HttpStatusCode statusCode = HttpStatusCode.Unauthorized)
        : base(statusCode, message)
    {
    }
}
