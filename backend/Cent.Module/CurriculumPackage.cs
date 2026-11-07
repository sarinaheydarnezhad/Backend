using System.Security.Cryptography;
using System.Text;

namespace Cent.Module.Curriculum;

public sealed record SourceFile(string Key, string Course, string File, string Title, string OriginalTitle, string Sha256, string Text);
public sealed record Chapter(string Key, string Course, string Title, int SortOrder);
public sealed record Topic(string Key, string ChapterKey, string Course, string Title, string EnglishTitle, int SortOrder, bool Foundation);
public sealed record SourceSection(string Key, string SourceKey, string? TopicKey, string Heading, int SortOrder, string Text, string Html);
public sealed record Exercise(string Key, string SourceKey, string SectionKey, string? TopicKey, string Course, string Text,
    string[] Options, int? CorrectAnswer, string Solution, string Difficulty, string? FigureSvg, string Status);
public sealed record BankQuestion(string Key, string Course, string TopicKey, string SectionKey, string? ExerciseKey,
    string Text, string[] Options, int CorrectAnswer, string Explanation, string Difficulty, string? FigureSvg, string Origin);
public sealed record ReviewFinding(string Key, string Action, string Reason);
public sealed record Corpus(List<SourceFile> Sources, List<Chapter> Chapters, List<Topic> Topics, List<SourceSection> Sections, List<Exercise> Exercises)
{
    public List<BankQuestion> Questions { get; set; } = [];
    public List<ReviewFinding> Findings { get; set; } = [];
}
public static class CurriculumIdentity
{
    public static Guid Id(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes("cent-curriculum-v1:" + key))[..16]);
    public static readonly (string Code, string Title, string EnglishTitle)[] Courses = [
        ("math", "ریاضی", "Mathematics"), ("logic", "منطق و تحلیل داده", "Logic and data interpretation"),
        ("biology", "زیست‌شناسی", "Biology"), ("chemistry", "شیمی", "Chemistry"), ("physics", "فیزیک", "Physics")
    ];
}
