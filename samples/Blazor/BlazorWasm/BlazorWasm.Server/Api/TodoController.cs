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
        // Get all item keys for this owner.
        var keys =
            await _factory.GetGrain<ITodoManagerGrain>(ownerKey)
                          .GetAllAsync();

        // Fast path for empty owner.
        if (keys.Length is 0) return Array.Empty<TodoItem>();

        // Fan out and get all individual items in parallel.
        // Issue all requests at the same time.
        var tasks =
            keys.Select(key => _factory.GetGrain<ITodoGrain>(key).GetAsync())
                .ToList();

        // Compose the result as requests complete
        var result = new List<TodoItem>();
        for (var i = 0; i < keys.Length; ++i)
        {
            var item = await tasks[i];
            if (item is null) continue;
            result.Add(item);
        }

        return result;
    }

    [HttpPost]
    public async Task<ActionResult> PostAsync([FromBody] TodoItemModel model)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var item = new TodoItem(model.Key, model.Title, model.IsDone, model.OwnerKey);
        await _factory.GetGrain<ITodoGrain>(item.Key).SetAsync(item);
        return Ok();
    }

    public record class TodoItemModel(
        [Required] Guid Key,
        [Required] string Title,
        [Required] bool IsDone,
        [Required] Guid OwnerKey);
}
