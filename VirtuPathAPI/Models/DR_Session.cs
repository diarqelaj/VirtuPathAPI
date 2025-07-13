public class DR_Session
{
    public long   Id            { get; set; }      // PK (IDENTITY)
    public int    UserAId       { get; set; }
    public int    UserBId       { get; set; }
    public string RootKeyB64    { get; set; } = "";
    public string ChainKeyAB64  { get; set; } = "";
    public string ChainKeyBB64  { get; set; } = "";
    public int    PN_A          { get; set; }      // “previous N” per X3DH paper
    public int    PN_B          { get; set; }
    public int    N_A           { get; set; }
    public int    N_B           { get; set; }
    public DateTime CreatedUtc  { get; set; } = DateTime.UtcNow;
}