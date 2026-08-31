using System.Security.Cryptography;
using user_server.services.scaling;

namespace user_server.network;

/// <summary>
///     Serializes matching-delivery application per session and remembers accepted delivery ids,
///     making NATS retries idempotent while rejecting an id reused with different content.
/// </summary>
internal sealed class MatchingDeliveryReceiptCache(int capacity)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MatchingDeliveryReceipt> _receipts =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _receiptOrder = new();

    public MatchingDeliveryStatus ApplyOnce(
        MatchingDeliveryRequest request,
        Func<MatchingDeliveryStatus> apply)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(apply);

        string payloadFingerprint = Convert.ToHexString(SHA256.HashData(request.Payload));
        lock (_gate)
        {
            if (_receipts.TryGetValue(request.DeliveryId, out MatchingDeliveryReceipt? receipt))
            {
                return receipt.Matches(request, payloadFingerprint)
                    ? receipt.Status
                    : MatchingDeliveryStatus.InvalidRequest;
            }

            MatchingDeliveryStatus status = apply();
            if (status == MatchingDeliveryStatus.Accepted)
            {
                _receipts.Add(
                    request.DeliveryId,
                    MatchingDeliveryReceipt.Create(request, payloadFingerprint, status));
                _receiptOrder.Enqueue(request.DeliveryId);
                while (_receiptOrder.Count > capacity)
                {
                    string expiredDeliveryId = _receiptOrder.Dequeue();
                    _receipts.Remove(expiredDeliveryId);
                }
            }

            return status;
        }
    }

    private sealed record MatchingDeliveryReceipt(
        MatchingDeliveryKind Kind,
        long PlayerId,
        long MatchingId,
        string RequestId,
        string OwnerNodeId,
        string OwnerNodeGeneration,
        string OwnerSessionId,
        long OwnerSessionGeneration,
        int ProtocolId,
        string PayloadFingerprint,
        MatchingDeliveryStatus Status)
    {
        public static MatchingDeliveryReceipt Create(
            MatchingDeliveryRequest request,
            string payloadFingerprint,
            MatchingDeliveryStatus status)
        {
            return new MatchingDeliveryReceipt(
                request.Kind,
                request.PlayerId,
                request.MatchingId,
                request.RequestId,
                request.OwnerNodeId,
                request.OwnerNodeGeneration,
                request.OwnerSessionId,
                request.OwnerSessionGeneration,
                request.ProtocolId,
                payloadFingerprint,
                status);
        }

        public bool Matches(MatchingDeliveryRequest request, string payloadFingerprint)
        {
            return Kind == request.Kind &&
                   PlayerId == request.PlayerId &&
                   MatchingId == request.MatchingId &&
                   string.Equals(RequestId, request.RequestId, StringComparison.Ordinal) &&
                   string.Equals(OwnerNodeId, request.OwnerNodeId, StringComparison.Ordinal) &&
                   string.Equals(OwnerNodeGeneration, request.OwnerNodeGeneration, StringComparison.Ordinal) &&
                   string.Equals(OwnerSessionId, request.OwnerSessionId, StringComparison.Ordinal) &&
                   OwnerSessionGeneration == request.OwnerSessionGeneration &&
                   ProtocolId == request.ProtocolId &&
                   string.Equals(PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal);
        }
    }
}
