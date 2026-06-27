using Docovee.DS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Docovee.DS;

public class DocoveeDbContext : DbContext
{
    public DocoveeDbContext(DbContextOptions<DocoveeDbContext> options) : base(options)
    {
    }

    public DbSet<Doctor> Doctors => Set<Doctor>();
    public DbSet<InsuranceCarrier> InsuranceCarriers => Set<InsuranceCarrier>();
    public DbSet<DoctorInsurance> DoctorInsurances => Set<DoctorInsurance>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<SearchSession> SearchSessions => Set<SearchSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<Admin> Admins => Set<Admin>();
    public DbSet<DoctorPatientReview> DoctorPatientReviews => Set<DoctorPatientReview>();
    public DbSet<PollingQuestion> PollingQuestions => Set<PollingQuestion>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<DoctorOnboardingSession> DoctorOnboardingSessions => Set<DoctorOnboardingSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Doctor>(entity =>
        {
            entity.ToTable("doctors");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Specialty).HasMaxLength(150).IsRequired();
            entity.Property(e => e.SpecialtyCategory).HasMaxLength(150).IsRequired();
            entity.Property(e => e.City).HasMaxLength(100).IsRequired();
            entity.Property(e => e.State).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ZipCode).HasMaxLength(20).IsRequired();
            entity.Property(e => e.GoogleRating).HasPrecision(3, 2);
            entity.Property(e => e.AvatarInitials).HasMaxLength(5);
            entity.Property(e => e.TagLine).HasMaxLength(200);
            entity.Property(e => e.Location).HasMaxLength(200);
            entity.Property(e => e.PracticeName).HasMaxLength(200);
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.OfficePhoneNumber).HasMaxLength(30);
            entity.Property(e => e.PhotoUrl).HasColumnType("text");
            entity.Property(e => e.GmbPhotoLink).HasColumnType("text");
            entity.Property(e => e.SummaryOfReviews).HasColumnType("text");
            entity.Property(e => e.Top3Procedures).HasMaxLength(500);
            entity.Property(e => e.Niche).HasMaxLength(200);
            entity.Property(e => e.Username).HasMaxLength(100);
            entity.Property(e => e.PasswordHash).HasMaxLength(500);
            entity.Property(e => e.OnboardingProfileJson).HasColumnType("text");
            entity.HasIndex(e => e.Username).IsUnique();
        });

        modelBuilder.Entity<DoctorOnboardingSession>(entity =>
        {
            entity.ToTable("doctor_onboarding_sessions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SessionKey).IsUnique();
            entity.Property(e => e.ContextJson).HasColumnType("text").IsRequired();
        });

        modelBuilder.Entity<DoctorPatientReview>(entity =>
        {
            entity.ToTable("doctor_patient_reviews");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ReviewerName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ReviewText).HasColumnType("text").IsRequired();
            entity.HasOne(e => e.Doctor).WithMany(d => d.PatientReviews).HasForeignKey(e => e.DoctorId);
            entity.HasOne(e => e.Patient).WithMany().HasForeignKey(e => e.PatientId);
        });

        modelBuilder.Entity<PollingQuestion>(entity =>
        {
            entity.ToTable("polling_questions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Question).HasMaxLength(500).IsRequired();
            entity.Property(e => e.ValidationHint).HasMaxLength(500);
        });

        modelBuilder.Entity<InsuranceCarrier>(entity =>
        {
            entity.ToTable("insurance_carriers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(150).IsRequired();
            entity.Property(e => e.Code).HasMaxLength(50).IsRequired();
            entity.HasIndex(e => e.Code).IsUnique();
        });

        modelBuilder.Entity<DoctorInsurance>(entity =>
        {
            entity.ToTable("doctor_insurances");
            entity.HasKey(e => new { e.DoctorId, e.InsuranceCarrierId });
            entity.HasOne(e => e.Doctor).WithMany(d => d.DoctorInsurances).HasForeignKey(e => e.DoctorId);
            entity.HasOne(e => e.InsuranceCarrier).WithMany(i => i.DoctorInsurances).HasForeignKey(e => e.InsuranceCarrierId);
        });

        modelBuilder.Entity<Patient>(entity =>
        {
            entity.ToTable("patients");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).HasMaxLength(100).IsRequired();
            entity.Property(e => e.PasswordHash).HasMaxLength(500).IsRequired();
            entity.Property(e => e.FullName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Phone).HasMaxLength(30).IsRequired();
            entity.HasIndex(e => e.Username).IsUnique();
        });

        modelBuilder.Entity<SearchSession>(entity =>
        {
            entity.ToTable("search_sessions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SessionKey).IsUnique();
            entity.Property(e => e.Location).HasMaxLength(200);
            entity.Property(e => e.InsurancePlanText).HasMaxLength(200);
            entity.Property(e => e.Specialty).HasMaxLength(150);
            entity.Property(e => e.SearchNotes).HasMaxLength(500);
            entity.Property(e => e.MedicalIssuesSummary).HasColumnType("text");
            entity.Property(e => e.CommunicationStyle).HasMaxLength(50);
            entity.Property(e => e.AvailabilityPreference).HasMaxLength(50);
            entity.Property(e => e.SearchContextJson).HasColumnType("text");
            entity.HasOne(e => e.Patient).WithMany(p => p.SearchSessions).HasForeignKey(e => e.PatientId);
            entity.HasOne(e => e.InsuranceCarrier).WithMany().HasForeignKey(e => e.InsuranceCarrierId);
        });

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.ToTable("chat_messages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Role).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Content).HasColumnType("text").IsRequired();
            entity.HasOne(e => e.SearchSession).WithMany(s => s.ChatMessages).HasForeignKey(e => e.SearchSessionId);
        });

        modelBuilder.Entity<Admin>(entity =>
        {
            entity.ToTable("admins");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).HasMaxLength(100).IsRequired();
            entity.Property(e => e.PasswordHash).HasMaxLength(500).IsRequired();
            entity.HasIndex(e => e.Username).IsUnique();
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.ToTable("app_settings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Key).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Value).HasMaxLength(500).IsRequired();
            entity.HasIndex(e => e.Key).IsUnique();
        });
    }
}
