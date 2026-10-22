namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>ProcessorArchitectureSetting</summary>
public enum ProcessorArchitectureSetting
{
    /// <summary>Automatically detect the processor architecture from the target platform.</summary>
    AutoDetect,
    /// <summary>Use the processor architecture of the current system.</summary>
    UseSystem,
    /// <summary>Force 32-bit (x86) processor architecture.</summary>
    X86,
    /// <summary>Force 64-bit (x64) processor architecture.</summary>
    X64,
    /// <summary>Force ARM64 processor architecture.</summary>
    Arm64
}
