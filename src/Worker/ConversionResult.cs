namespace Jalyro.Convert.Worker;

/// <summary>
/// Worker exit codes. The Host maps these to human-readable failures; nothing
/// from a decoder ever reaches the user verbatim.
/// </summary>
internal static class ExitCode
{
    public const int Success            = 0;
    public const int BadArguments       = 1;
    public const int InputUnreadable    = 2;
    public const int UnsupportedFormat  = 3;
    public const int DecodeFailed       = 4;
    public const int EncodeFailed       = 5;
    public const int OutputWriteFailed  = 6;
    public const int RefusedUnsafePath  = 7;
}
