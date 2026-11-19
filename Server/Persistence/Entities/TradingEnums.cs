namespace StockLab.Persistence.Entities;

public enum UserType
{
    Guest = 1,
    Discord = 2,
    Local = 3
}

public enum OrderSide
{
    Buy = 1,
    Sell = 2
}

public enum OrderType
{
    Market = 1,
    Limit = 2
}

public enum OrderStatus
{
    Pending = 1,
    PartiallyFilled = 2,
    Filled = 3,
    Cancelled = 4,
    Rejected = 5
}
