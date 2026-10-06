using Microsoft.EntityFrameworkCore;

namespace Casl.Tests;

public enum WorkStatus { Open, InProgress, Done }

public class Project
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int OrgUnitId { get; set; }
    public bool Archived { get; set; }
}

public class Comment
{
    public int Id { get; set; }
    public int WorkItemId { get; set; }
    public string AuthorId { get; set; } = "";
    public bool Internal { get; set; }
}

public class WorkItem
{
    public int Id { get; set; }
    public string Summary { get; set; } = "";
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
    public string? AssigneeId { get; set; }
    public WorkStatus Status { get; set; }
    public int Priority { get; set; }
    public DateTime? DueAt { get; set; }
    public List<string> Labels { get; set; } = [];
    public List<Comment> Comments { get; set; } = [];
}

public class TestDb(DbContextOptions<TestDb> options) : DbContext(options)
{
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<Project> Projects => Set<Project>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkItem>().Property(w => w.Status).HasConversion<string>();
        modelBuilder.Entity<WorkItem>().HasMany(w => w.Comments).WithOne().HasForeignKey(c => c.WorkItemId);
    }
}

public static class Subjects
{
    public static SubjectRegistry Create() => new SubjectRegistry()
        .Add<WorkItem>("WorkItem", s => s
            .ConditionFields("id", "summary", "projectId", "assigneeId", "status", "priority", "dueAt", "labels",
                "project.orgUnitId", "project.archived", "comments", "comments.authorId", "comments.internal")
            .Fields("summary", "status", "assigneeId", "priority", "labels"))
        .Add<Project>("Project", s => s.ConditionFields("id", "orgUnitId", "archived").Fields("name"));
}
