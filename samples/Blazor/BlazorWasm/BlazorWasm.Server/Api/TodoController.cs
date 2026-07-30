using Microsoft.AspNetCore.Mvc;
using BlazorWasm.Grains;
using BlazorWasm.Models;
using System.ComponentModel.DataAnnotations;

namespace Sample.Silo.Api;

[ApiController]
[Route("api/todo")]
public class TodoController : ControllerBase
{
    private readonly IGrainFactory _factory;

    public TodoController(IGrainFactory factory) => _factory = factory;

    [HttpGet("{itemKey}")]
    public Task<TodoItem?> GetAsync([Required] Guid itemKey) =>
        _factory.GetGrain<ITodoGrain>(itemKey).GetAsync();

    [HttpDelete("{itemKey}")]
    public Task DeleteAsync([Required] Guid itemKey) =>
        _factory.GetGrain<ITodoGrain>(itemKey).ClearAsync();

    [HttpGet("list/{ownerKey}", Name = "list")]
    public async Task<IEnumerable<TodoItem>> ListAsync([Required] Guid ownerKey)
    {
        // get all the todo item keys for this owner
        var itemKeys = await _factory
            .GetGrain<ITodoManagerGrain>(ownerKey)
            .GetAllAsync();

        // fan out to get the individual items from the cluster in parallel
        // issue all individual requests at the same time
        var tasks = itemKeys
            .Select(async itemId =>
            {
                var item = await _factory
                    .GetGrain<ITodoGrain>(itemId)
                    .GetAsync();

                // we can get a null result if the individual grain failed to unregister
                // in this case we can finish the job here
                if (item is null)
                {
                    await _factory
                        .GetGrain<ITodoManagerGrain>(ownerKey)
                        .UnregisterAsync(itemId);
                }

                return item;
            });

        var result = await Task.WhenAll(tasks);

        return result.OfType<TodoItem>();
    }

    [HttpPost]
    public async Task<ActionResult> PostAsync([FromBody] TodoItemModel model)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var item = new TodoItem(model.Key, model.Title, model.IsDone, model.OwnerKey, DateTime.UtcNow);
        await _factory.GetGrain<ITodoGrain>(item.Key).SetAsync(item);
        return Ok();
    }

    public record class TodoItemModel(
        [Required] Guid Key,
        [Required] string Title,
        [Required] bool IsDone,
        [Required] Guid OwnerKey);
}
