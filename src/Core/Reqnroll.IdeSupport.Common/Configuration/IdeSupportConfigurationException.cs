using System;

namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>IdeSupportConfigurationException</summary>
public class IdeSupportConfigurationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="IdeSupportConfigurationException"/> class.</summary>
    public IdeSupportConfigurationException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="IdeSupportConfigurationException"/> class.</summary>
    public IdeSupportConfigurationException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="IdeSupportConfigurationException"/> class.</summary>
    public IdeSupportConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
