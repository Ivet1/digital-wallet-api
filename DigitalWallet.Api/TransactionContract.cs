namespace DigitalWallet.Api.Models;

public record CreateTransactionRequest(string? Description, decimal Amount, string? Category = null);

public record TransactionDto(Guid Id, string Description, decimal Amount, string Type, string Category, DateTime CreatedAt);