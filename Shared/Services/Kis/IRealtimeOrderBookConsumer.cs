using StockLab.Dtos;

namespace StockLab.Services.Kis;

/// <summary>
/// 실시간 호가 수신 시 후속 처리(예: 서버 측 체결 매칭)를 수행하는 선택적 소비자 인터페이스.
/// </summary>
public interface IRealtimeOrderBookConsumer
{
    Task OnOrderBookReceivedAsync(RealtimeStockStatus status, CancellationToken cancellationToken = default);
}

