using System;

namespace LetterboxdSync.Serializd;

/// <summary>Serializd rejected the account's credentials (401/403 on login), as opposed to a network or server error.</summary>
public class SerializdAuthException : Exception
{
    public SerializdAuthException(string message)
        : base(message)
    {
    }
}
