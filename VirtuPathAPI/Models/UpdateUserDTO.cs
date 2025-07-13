// File: Models/UpdateUserDto.cs
namespace VirtuPathAPI.Models
{
    public class UpdateUserDto
    {
        public string FullName { get; set; } = null!;
        public string Username { get; set; } = null!;
        public string Email { get; set; } = null!;
        public bool IsOfficial { get; set; }
        public bool IsVerified { get; set; }
    }
}
