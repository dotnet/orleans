#nullable enable
namespace Orleans.AdvancedReminders;

internal readonly record struct CronFieldExpression(string Text, bool CanCombine);
