namespace SneakyFlow.Models;

public sealed record PeerItem(string Endpoint, string Client, string Role, string Status,
    long DownloadSpeed, long UploadSpeed, int OutstandingRequests);
