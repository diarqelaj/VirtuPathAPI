public class DR_SkippedKey
{
    public long   SessionId     { get; set; }
    public int    N             { get; set; }      // message number
    public string MessageKeyB64 { get; set; } = "";
    public DateTime ExpiresUtc  { get; set; }
    public DR_Session? Session  { get; set; }
}