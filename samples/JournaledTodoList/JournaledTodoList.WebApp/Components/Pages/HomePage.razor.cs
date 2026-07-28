using System.Collections.Immutable;
using JournaledTodoList.WebApp.Grains;
using JournaledTodoList.WebApp.Services;

namespace JournaledTodoList.WebApp.Components.Pages;

public partial class HomePage(TodoListService todoListService) : ITodoListRegistryObserver, IDisposable
{
    private IDisposable? subscription;
    private string newListName = "";

    private ImmutableArray<TodoListReference> TodoLists { get; set; } = [];

    protected override async Task OnInitializedAsync()
    {
        subscription = await todoListService.SubscribeAsync(this);
    }

    public void Dispose()
    {
        subscription?.Dispose();
    }

    async Task ITodoListRegistryObserver.OnTodoListsChanged(ImmutableArray<TodoListReference> todoLists) => await InvokeAsync(() =>
    {
        TodoLists = todoLists;
        StateHasChanged();
    });

    private async Task CreateNewList(string listName)
    {
        if (string.IsNullOrWhiteSpace(listName) || TodoLists.Any(x => x.Name == listName))
        {
            return;
        }

        await todoListService.CreateTodoListAsync(listName);
        newListName = "";
    }
}
