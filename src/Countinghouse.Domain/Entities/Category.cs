namespace Countinghouse.Domain.Entities;

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public CategoryType CategoryType { get; set; }

    public int? ParentCategoryId { get; set; }
    public Category? ParentCategory { get; set; }
    public List<Category> Subcategories { get; set; } = [];

    public string FullName => ParentCategory is null ? Name : $"{ParentCategory.Name}:{Name}";
}
