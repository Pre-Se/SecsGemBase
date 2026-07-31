namespace SecsGemBaseItems.SecsGemParameters;

/// <summary>
/// Contains the session types for the SECS/GEM protocol.
/// </summary>
public static class SecsGemSessionType
{
    //using a dictionary to store the session types because the session types are fixed and will not change,
    //but the user must also be able to extend the session types if needed according to the secs/gem standard
    private static readonly Dictionary<byte, string?> SessionTypeDict = new()
        {
            { 0, SessionTypeStrings.DataMessage },
            { 1, SessionTypeStrings.SelectRequest },
            { 2, SessionTypeStrings.SelectResponse },
            { 3, SessionTypeStrings.DeselectRequest },
            { 4, SessionTypeStrings.DeselectResponse },
            { 5, SessionTypeStrings.LinktestRequest },
            { 6, SessionTypeStrings.LinktestResponse },
            { 7, SessionTypeStrings.RejectRequest },
            { 9, SessionTypeStrings.SeparateRequest }
        };

    public static bool TryGetSessionType(byte sessionType, out string? sessionTypeName)
    {
        return SessionTypeDict.TryGetValue(sessionType, out sessionTypeName);
    }

    public static byte GetSessionType(string sessionType)
    {
        return SessionTypeDict.FirstOrDefault(x => x.Value == sessionType).Key;
    }

    /// <summary>
    /// Contains the session types for the SECS/GEM protocol to make the code more readable and
    /// not having to hardcode the session type string name everytime it is used.
    /// </summary>
    public static class SessionTypeStrings
    {
        public const string DataMessage = "Data Message";
        public const string SelectRequest = "Select Request";
        public const string SelectResponse = "Select Response";
        public const string DeselectRequest = "Deselect Request";
        public const string DeselectResponse = "Deselect Response";
        public const string LinktestRequest = "Linktest Request";
        public const string LinktestResponse = "Linktest Response";
        public const string RejectRequest = "Reject Request";
        public const string SeparateRequest = "Separate Request";
    }
}