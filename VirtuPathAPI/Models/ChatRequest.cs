public class ChatRequest
{
  public int Id { get; set; }
  public int SenderId { get; set; }
  public int ReceiverId { get; set; }
  public DateTime SentAt { get; set; }
  public bool IsAccepted { get; set; }
}
