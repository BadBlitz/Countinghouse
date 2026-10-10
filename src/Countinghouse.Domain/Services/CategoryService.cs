using Microsoft.EntityFrameworkCore;
using Countinghouse.Domain.Data;
using Countinghouse.Domain.Entities;

namespace Countinghouse.Domain.Services;

public class CategoryService(IDbContextFactory<CountinghouseContext> factory)
{
    public async Task<List<Category>> GetCategoriesAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Categories.AsNoTracking()
            .Include(c => c.ParentCategory)
            .OrderBy(c => c.ParentCategory!.Name ?? c.Name)
            .ThenBy(c => c.ParentCategoryId == null ? 0 : 1)
            .ThenBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<(Category? Category, string? Error)> CreateAsync(
        string name, CategoryType type, int? parentCategoryId)
    {
        name = name.Trim();
        if (name.Length == 0)
            return (null, "Name is required.");

        await using var db = await factory.CreateDbContextAsync();
        var exists = await db.Categories
            .AnyAsync(c => c.ParentCategoryId == parentCategoryId && c.Name == name);
        if (exists)
            return (null, $"A category named \"{name}\" already exists there.");

        var category = new Category { Name = name, CategoryType = type, ParentCategoryId = parentCategoryId };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return (category, null);
    }

    public async Task<string?> UpdateAsync(Category edited)
    {
        edited.Name = edited.Name.Trim();
        if (edited.Name.Length == 0)
            return "Name is required.";

        await using var db = await factory.CreateDbContextAsync();
        var duplicate = await db.Categories.AnyAsync(c =>
            c.Id != edited.Id && c.ParentCategoryId == edited.ParentCategoryId && c.Name == edited.Name);
        if (duplicate)
            return $"A category named \"{edited.Name}\" already exists there.";

        var category = await db.Categories.FirstAsync(c => c.Id == edited.Id);
        category.Name = edited.Name;
        category.CategoryType = edited.CategoryType;
        await db.SaveChangesAsync();
        return null;
    }

    // Deleting a category leaves its transactions uncategorized (FK is SetNull).
    // Categories with subcategories can't be deleted until the children are removed.
    public async Task<string?> DeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var hasChildren = await db.Categories.AnyAsync(c => c.ParentCategoryId == id);
        if (hasChildren)
            return "Delete or move its subcategories first.";

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id);
        if (category is null)
            return null;

        var inUse = await db.Transactions.Where(t => t.CategoryId == id).ToListAsync();
        foreach (var txn in inUse)
            txn.CategoryId = null;

        db.Categories.Remove(category);
        await db.SaveChangesAsync();
        return null;
    }
}
