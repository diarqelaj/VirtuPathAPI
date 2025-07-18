public class ChatConversation
{
    public int    Id            { get; set; }
    public int    UserAId       { get; set; }   // min(user1,user2)
    public int    UserBId       { get; set; }   // max(user1,user2)
    public string SymmetricKey  { get; set; }   // Base64(32‐byte random)
}
