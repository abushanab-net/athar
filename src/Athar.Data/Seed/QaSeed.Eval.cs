using Athar.Domain.Memory;
using Athar.Domain.Model;

namespace Athar.Data.Seed;

/// <summary>
/// T082: the bilingual evaluation corpus (AT-40, BRD G3). Each answer sits next to near-miss distractors (other numbers,
/// neighbouring topics, the other language), with traps the caller must never see: a confidential twin, another user's
/// personal memory, a page in a space they can't open, and company memories the pilot overrides.
/// The 30 questions are in <c>tests/Athar.IntegrationTests/Eval/athar-eval.json</c> and refer to these by name.
/// Chunks are built like the worker builds them (normalized), with fixed IDs so a reset reproduces them (AT-55).
/// </summary>
public static partial class QaSeed
{
    private static Guid EvalId(int n) => Id(0x7e000 + n);

    /// <summary>Evaluation items by the names the question file uses.</summary>
    public static readonly IReadOnlyDictionary<string, Guid> Eval = new Dictionary<string, Guid>
    {
        ["E01"] = EvalId(1), ["E02"] = EvalId(2), ["E03"] = EvalId(3), ["E04"] = EvalId(4), ["E05"] = EvalId(5), ["E06"] = EvalId(6),
        ["E07"] = EvalId(7), ["E08"] = EvalId(8), ["E09"] = EvalId(9), ["E10"] = EvalId(10), ["E11"] = EvalId(11), ["E12"] = EvalId(12),
        ["E13"] = EvalId(13), ["E14"] = EvalId(14), ["E15"] = EvalId(15), ["E16"] = EvalId(16), ["E17"] = EvalId(17), ["E18"] = EvalId(18),
        ["E19"] = EvalId(19), ["E20"] = EvalId(20), ["E21"] = EvalId(21), ["E22"] = EvalId(22), ["E23"] = EvalId(23), ["E24"] = EvalId(24),
        ["E25"] = EvalId(25), ["E26"] = EvalId(26), ["E27"] = EvalId(27), ["E28"] = EvalId(28), ["E29"] = EvalId(29), ["E30"] = EvalId(30),
        ["P1"] = EvalId(101), ["P2"] = EvalId(102), ["P3"] = EvalId(103), ["P4"] = EvalId(104), ["P5"] = EvalId(105), ["P6"] = EvalId(106),
    };

    private static void AddEval(AtharDbContext db)
    {
        var chunk = 0x7e800;
        void Keys(Chunk c, Memory? m = null, Guid? space = null, bool confidential = false)
        {
            c.Id = Id(chunk++);
            c.OrgId = Org;
            if (m is not null) (c.Scope, c.OwnerUserId, c.OwnerTeamId, c.OwnerProjectId) = (m.Scope, m.OwnerUserId, m.OwnerTeamId, m.OwnerProjectId);
            c.SpaceId = space;
            c.IsConfidential = confidential;
        }
        void Memory(string name, Scope scope, Guid? owner, MemoryType type, string title, string body, Guid by, bool confidential = false)
        {
            var id = Eval[name];
            var m = new Memory
            {
                Id = id, OrgId = Org, Scope = scope, Type = type, Title = title, BodyMd = body, Status = MemoryStatus.Active,
                OwnerUserId = scope == Scope.Personal ? owner : null, OwnerTeamId = scope == Scope.Team ? owner : null,
                OwnerProjectId = scope == Scope.Project ? owner : null, Overridable = type != MemoryType.Policy, Origin = "seed",
                CreatedByUser = by, IsConfidential = confidential,
            };
            db.Memories.Add(m);
            db.MemoryVersions.Add(new MemoryVersion { Id = Id(chunk++), OrgId = Org, MemoryId = id, Revision = 1, Title = title, BodyMd = body, ChangedBy = by });
            db.Chunks.AddRange(Chunker.Build(ChunkOwner.Memory, id, 1, title, body, c => Keys(c, m, confidential: confidential)));
        }
        void Page(string name, Guid space, string title, string text, bool confidential = false)
        {
            var id = Eval[name];
            db.Pages.Add(new Page { Id = id, OrgId = Org, SpaceId = space, Position = 100 + chunk % 100, Title = title, ContentMd = text, Revision = 1 });
            db.PageRevisions.Add(new PageRevision { Id = Id(chunk++), OrgId = Org, PageId = id, Revision = 1, Contributors = [Admin] });
            db.Chunks.AddRange(Chunker.Build(ChunkOwner.Page, id, 1, title, text, c => Keys(c, space: space, confidential: confidential)));
        }

        // Deploys: the answer and a near miss.
        Memory("E01", Scope.Company, null, MemoryType.Policy, "Production deploys need two approvals",
            "Every production deploy needs approval from two engineers, one of them from the on-call rotation.", KnowledgeOwner);
        Memory("E02", Scope.Company, null, MemoryType.Policy, "Staging deploys need one approval",
            "Staging deploys need a single approval from any engineer.", KnowledgeOwner);
        // Retention, in Arabic: backups 35 days vs application logs 14 days.
        Memory("E03", Scope.Company, null, MemoryType.Policy, "مدة الاحتفاظ بالنسخ الاحتياطية",
            "تُحفظ النسخ الاحتياطية لقاعدة البيانات مدة خمسة وثلاثين يومًا ثم تُحذف.", KnowledgeOwner);
        Memory("E04", Scope.Company, null, MemoryType.Fact, "مدة الاحتفاظ بسجلات التطبيق",
            "تُحفظ سجلات التطبيق أربعة عشر يومًا فقط.", KnowledgeOwner);
        // API conventions.
        Memory("E05", Scope.Company, null, MemoryType.Convention, "API errors use problem details",
            "Every HTTP API returns errors as RFC 9457 problem details with a stable code field.", KnowledgeOwner);
        Memory("E06", Scope.Company, null, MemoryType.Convention, "Log lines are JSON",
            "Services write structured JSON log lines that carry a trace id.", KnowledgeOwner);
        // Key and password rotation, in Arabic.
        Memory("E07", Scope.Company, null, MemoryType.Procedure, "خطوات تدوير مفاتيح التشفير",
            "تُدوَّر مفاتيح التشفير كل تسعين يومًا: أنشئ المفتاح الجديد، ثم انقل الخدمات إليه، ثم ألغِ المفتاح القديم.", KnowledgeOwner);
        Memory("E08", Scope.Company, null, MemoryType.Procedure, "خطوات تغيير كلمات المرور المشتركة",
            "تُغيَّر كلمات مرور الحسابات المشتركة كل ستين يومًا ويُبلَّغ الفريق بعد التغيير.", KnowledgeOwner);
        // Glossary, one per language.
        Memory("E09", Scope.Company, null, MemoryType.Glossary, "SLO means service level objective",
            "An SLO is the reliability target we promise internally, for example 99.9% availability per month.", KnowledgeOwner);
        Memory("E10", Scope.Company, null, MemoryType.Glossary, "معنى الجاهزية",
            "الجاهزية تعني أن الخدمة تجيب على فحص الصحة خلال ثانيتين.", KnowledgeOwner);
        // Decisions.
        Memory("E11", Scope.Company, null, MemoryType.Decision, "The job queue lives in Postgres",
            "We keep the job queue in Postgres with leases, so there is no extra message broker to run.", KnowledgeOwner);
        Memory("E12", Scope.Company, null, MemoryType.Decision, "Rendered pages are cached for five minutes",
            "The web tier caches rendered pages for five minutes to keep page loads fast.", KnowledgeOwner);
        Memory("E13", Scope.Company, null, MemoryType.Incident, "انقطاع الشبكة في أغسطس",
            "في أغسطس انقطعت الشبكة ساعتين بسبب تحديث خاطئ لجدار الحماية، فأضفنا مراجعة ثانية لكل تغيير في الجدار.", KnowledgeOwner);
        // Team and project scope.
        Memory("E14", Scope.Team, PlatformTeam, MemoryType.Procedure, "Platform on-call swaps happen on Fridays",
            "Platform engineers swap on-call shifts on Friday afternoons in the team channel.", Admin);
        Memory("E15", Scope.Project, PilotProject, MemoryType.Convention, "Pilot branch names",
            "In the pilot, branches are named feature/ followed by the ticket number.", Admin);
        // Override traps: the pilot shadows these company memories (in each language).
        Memory("E16", Scope.Company, null, MemoryType.Convention, "Code review turnaround",
            "Reviewers respond to a pull request within one business day.", KnowledgeOwner);
        Memory("E17", Scope.Project, PilotProject, MemoryType.Convention, "Code review turnaround",
            "In the pilot, reviewers respond to pull requests within four hours.", Admin);
        Memory("E18", Scope.Company, null, MemoryType.Convention, "ساعات العمل الأساسية",
            "ساعات العمل الأساسية من التاسعة صباحًا إلى الثالثة عصرًا.", KnowledgeOwner);
        Memory("E19", Scope.Project, PilotProject, MemoryType.Convention, "ساعات العمل الأساسية",
            "في المشروع التجريبي ساعات العمل الأساسية من العاشرة صباحًا إلى الرابعة عصرًا.", Admin);
        db.MemoryShadows.AddRange(
            new MemoryShadow { Id = EvalId(201), OrgId = Org, ProjectId = PilotProject, CompanyMemoryId = Eval["E16"], ShadowMemoryId = Eval["E17"], BaseRevision = 1 },
            new MemoryShadow { Id = EvalId(202), OrgId = Org, ProjectId = PilotProject, CompanyMemoryId = Eval["E18"], ShadowMemoryId = Eval["E19"], BaseRevision = 1 });
        // Permission traps: a confidential twin, and someone else's personal memory.
        Memory("E20", Scope.Company, null, MemoryType.Fact, "Performance review calibration",
            "Performance reviews are calibrated with HR in May before ratings are shared.", Owner, confidential: true);
        Memory("E21", Scope.Company, null, MemoryType.Fact, "Performance reviews happen in June",
            "Performance reviews happen every June; managers share written feedback.", KnowledgeOwner);
        Memory("E22", Scope.Personal, KnowledgeOwner, MemoryType.Preference, "My shell is fish",
            "I use the fish shell with a minimal prompt on my laptop.", KnowledgeOwner);
        Memory("E23", Scope.Personal, Member, MemoryType.Preference, "My shell is zsh",
            "I use zsh with oh-my-zsh on my laptop.", Member);
        Memory("E24", Scope.Company, null, MemoryType.Policy, "بدل السكن للمديرين",
            "يُصرف بدل السكن للمديرين في بداية كل ربع سنة.", Owner, confidential: true);
        Memory("E25", Scope.Company, null, MemoryType.Policy, "بدل المواصلات",
            "يُصرف بدل المواصلات شهريًا لجميع الموظفين مع الراتب.", KnowledgeOwner);
        // More facts in both languages, for cross-language questions.
        Memory("E26", Scope.Company, null, MemoryType.Procedure, "Incident severity levels",
            "SEV1 means customers are down, SEV2 means degraded service, SEV3 affects internal tools only.", KnowledgeOwner);
        Memory("E27", Scope.Company, null, MemoryType.Fact, "رقم الطوارئ الداخلي",
            "رقم الطوارئ الداخلي للمناوب هو 7000.", KnowledgeOwner);
        Memory("E28", Scope.Company, null, MemoryType.Convention, "تسمية قواعد البيانات",
            "تُسمّى قواعد البيانات بأحرف إنجليزية صغيرة مع شرطة سفلية بين الكلمات.", KnowledgeOwner);
        Memory("E29", Scope.Company, null, MemoryType.Fact, "Office Wi-Fi network",
            "The office Wi-Fi network is called Athar-Staff; guests use Athar-Guest.", KnowledgeOwner);
        Memory("E30", Scope.Company, null, MemoryType.Decision, "اختيار مكتبة الواجهات",
            "اخترنا مكتبة MudBlazor لواجهات المستخدم لأنها مرخصة بترخيص MIT ولها مجتمع كبير.", KnowledgeOwner);

        // Pages: two the member reads, and traps in a space they can't open and in a confidential one.
        Page("P1", EngWiki, "Release checklist", "Tag the release, update the changelog, and announce it in the releases channel.");
        Page("P2", EngWiki, "دليل إعداد بيئة التطوير", "ثبّت .NET 10 و Docker، ثم شغّل المضيف بالأمر dotnet run.");
        Page("P3", OpsHandbook, "Leave policy", "Leave requests need two weeks of notice and a manager's sign-off.");
        Page("P4", EngWiki, "Requesting leave", "Request leave through the HR portal; your manager approves it within three days.");
        Page("P5", EngConfidential, "Hiring budget", "The hiring budget covers four engineers this year.", confidential: true);
        Page("P6", EngWiki, "Hiring process", "Candidates meet two engineers and a manager, then get an answer within a week.");
    }
}
