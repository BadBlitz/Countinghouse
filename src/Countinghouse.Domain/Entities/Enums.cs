namespace Countinghouse.Domain.Entities;

public enum AccountType
{
    Checking,
    Savings,
    CreditCard,
    Mortgage,
    Investment,
    Manual
}

public enum ClearedStatus
{
    Uncleared,
    Cleared,
    Reconciled
}

public enum TransactionSource
{
    Manual,
    SimpleFin
}

public enum CategoryType
{
    Income,
    Expense,
    Transfer
}
