using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using JournaledTodoList.WebApp.Grains;
using JournaledTodoList.WebApp.Grains.Events;
using JournaledTodoList.WebApp.Services;
using Microsoft.AspNetCore.Components;

namespace JournaledTodoList.WebApp.Components.Pages;

public partial class TodoListPage(TodoListService todoService)
{
    private string newItemTitle = "";
    private TodoList? todoList;
    private ImmutableArray<TodoListEvent> history;
    private DateTimeOffset? currentViewTimestamp;

    [MemberNotNullWhen(true, nameof(currentViewTimestamp))]
    private bool IsViewingHistory => !history.IsDefaultOrEmpty && currentViewTimestamp < history[^1].Timestamp;

    [Parameter, EditorRequired]
    public required string ListId { get; set; }

    protected override async Task OnParametersSetAsync()
    {
        if (todoList?.Name != ListId)
        {
            currentViewTimestamp = null;
            todoList = null;
            await LoadTodoList();
        }
    }

    private async Task LoadTodoList()
    {
        todoList = await todoService.GetTodoListAsync(ListId);
        history = await todoService.GetTodoListHistoryAsync(ListId);
    }

    private async Task AddItem()
    {
        if (string.IsNullOrWhiteSpace(newItemTitle) || IsViewingHistory)
        {
            return;
        }

        await todoService.AddTodoItemAsync(ListId, newItemTitle);
        newItemTitle = "";
        await LoadTodoList();
    }

    private async Task UpdateItem(int itemId, string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || IsViewingHistory)
        {
            return;
        }

        await todoService.UpdateTodoItemAsync(ListId, itemId, title);
        await LoadTodoList();
    }

    private async Task ToggleItem(int itemId)
    {
        if (IsViewingHistory)
        {
            return;
        }

        await todoService.ToggleTodoItemAsync(ListId, itemId);
        await LoadTodoList();
    }

    private async Task RemoveItem(int itemId)
    {
        if (IsViewingHistory)
        {
            return;
        }

        await todoService.RemoveTodoItemAsync(ListId, itemId);
        await LoadTodoList();
    }

    private async Task ViewAtTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp == history[^1].Timestamp)
        {
            currentViewTimestamp = null;
        }
        else
        {
            currentViewTimestamp = timestamp;
            todoList = await todoService.GetTodoListAtTimestampAsync(ListId, timestamp);
        }
    }

    private async Task ReturnToCurrentVersion()
    {
        currentViewTimestamp = null;
        await LoadTodoList();
    }

    private bool IsCurrentHistoryItem(TodoListEvent item)
        => currentViewTimestamp.HasValue
        ? item.Timestamp == currentViewTimestamp
        : history[^1] == item;
}
