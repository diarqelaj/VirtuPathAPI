using MongoDB.Driver;
using VirtuPathAPI.Models;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Data
{
    public interface IFeedbackRepository
    {
        Task<string> CreateAsync(Feedback f);
        Task<List<Feedback>> GetAllAsync(int skip = 0, int take = 50);
        Task<Feedback?> GetByIdAsync(string id);
        Task<List<Feedback>> GetByUserAsync(string userId, int skip = 0, int take = 50);
        Task<bool> UpdateStatusAsync(string id, string status);
        Task<long> DeleteAsync(string id);
    }

    public class MongoFeedbackRepository : IFeedbackRepository
    {
        private readonly IMongoCollection<Feedback> _col;

        public MongoFeedbackRepository(IMongoDatabase db)
        {
            _col = db.GetCollection<Feedback>("Feedbacks");

            // Recommended indexes (runs once; Atlas is idempotent on same keys)
            var indexes = new List<CreateIndexModel<Feedback>>
            {
                new CreateIndexModel<Feedback>(
                    Builders<Feedback>.IndexKeys.Descending(x => x.CreatedAt)),
                new CreateIndexModel<Feedback>(
                    Builders<Feedback>.IndexKeys.Ascending(x => x.UserId).Descending(x => x.CreatedAt)),
                new CreateIndexModel<Feedback>(
                    Builders<Feedback>.IndexKeys.Ascending(x => x.Rating))
            };
            _col.Indexes.CreateMany(indexes);
        }

        public async Task<string> CreateAsync(Feedback f)
        {
            f.CreatedAt = DateTime.UtcNow;
            await _col.InsertOneAsync(f);
            return f.Id!;
        }



        public Task<Feedback?> GetByIdAsync(string id) =>
            _col.Find(x => x.Id == id).FirstOrDefaultAsync();

        public Task<List<Feedback>> GetAllAsync(int skip = 0, int take = 50) =>
    _col.Find(FilterDefinition<Feedback>.Empty)
        .SortByDescending(x => x.CreatedAt)
        .Skip(skip)
        .Limit(take)
        .ToListAsync();

        public Task<List<Feedback>> GetByUserAsync(string userId, int skip = 0, int take = 50) =>
            _col.Find(x => x.UserId == userId)
                .SortByDescending(x => x.CreatedAt)
                .Skip(skip)
                .Limit(take)
                .ToListAsync();


        public async Task<bool> UpdateStatusAsync(string id, string status)
        {
            var upd = Builders<Feedback>.Update.Set(x => x.Status, status);
            var res = await _col.UpdateOneAsync(x => x.Id == id, upd);
            return res.ModifiedCount == 1;
        }

        public Task<long> DeleteAsync(string id) =>
            _col.DeleteOneAsync(x => x.Id == id).ContinueWith(t => (long)t.Result.DeletedCount);
    }
}
