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
    public DbSet<InsurancePlan> InsurancePlans => Set<InsurancePlan>();
    public DbSet<DoctorInsurance> DoctorInsurances => Set<DoctorInsurance>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<SearchSession> SearchSessions => Set<SearchSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<Admin> Admins => Set<Admin>();
    public DbSet<DoctorPatientReview> DoctorPatientReviews => Set<DoctorPatientReview>();
    public DbSet<PollingQuestion> PollingQuestions => Set<PollingQuestion>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<DoctorOnboardingSession> DoctorOnboardingSessions => Set<DoctorOnboardingSession>();
    public DbSet<DoctorLanguage> DoctorLanguages => Set<DoctorLanguage>();
    public DbSet<DoctorDoctorLanguage> DoctorDoctorLanguages => Set<DoctorDoctorLanguage>();
    public DbSet<PatientDoctorContactView> PatientDoctorContactViews => Set<PatientDoctorContactView>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<DoctorLocation> DoctorLocations => Set<DoctorLocation>();
    public DbSet<PmsConnection> PmsConnections => Set<PmsConnection>();
    public DbSet<PmsExternalRef> PmsExternalRefs => Set<PmsExternalRef>();

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
            entity.Property(e => e.VideoUrl).HasColumnType("text");
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
            entity.Property(e => e.WaitingTime).HasMaxLength(50);
            entity.Property(e => e.Recommendation).HasMaxLength(50);
            entity.HasOne(e => e.Doctor).WithMany(d => d.PatientReviews).HasForeignKey(e => e.DoctorId);
            entity.HasOne(e => e.Patient).WithMany().HasForeignKey(e => e.PatientId);
        });

        modelBuilder.Entity<PollingQuestion>(entity =>
        {
            entity.ToTable("polling_questions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Question).HasMaxLength(500).IsRequired();
            entity.Property(e => e.ValidationHint).HasMaxLength(500);
            entity.Property(e => e.MatchWeightLabel).HasMaxLength(50);
        });

        modelBuilder.Entity<InsuranceCarrier>(entity =>
        {
            entity.ToTable("insurance_carriers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(150).IsRequired();
            entity.Property(e => e.Code).HasMaxLength(50).IsRequired();
            entity.HasIndex(e => e.Code).IsUnique();
        });

        modelBuilder.Entity<InsurancePlan>(entity =>
        {
            entity.ToTable("insurance_plans");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(e => new { e.InsuranceCarrierId, e.Name }).IsUnique();
            entity.HasOne(e => e.InsuranceCarrier)
                .WithMany(c => c.Plans)
                .HasForeignKey(e => e.InsuranceCarrierId)
                .OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<DoctorLanguage>(entity =>
        {
            entity.ToTable("doctor_languages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<DoctorDoctorLanguage>(entity =>
        {
            entity.ToTable("doctor_doctor_languages");
            entity.HasKey(e => new { e.DoctorId, e.DoctorLanguageId });
            entity.HasOne(e => e.Doctor).WithMany(d => d.DoctorLanguages).HasForeignKey(e => e.DoctorId);
            entity.HasOne(e => e.DoctorLanguage).WithMany(l => l.Doctors).HasForeignKey(e => e.DoctorLanguageId);
        });

        modelBuilder.Entity<PatientDoctorContactView>(entity =>
        {
            entity.ToTable("patient_doctor_contact_views");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.PatientId, e.DoctorId }).IsUnique();
            entity.HasOne(e => e.Patient).WithMany(p => p.DoctorContactViews).HasForeignKey(e => e.PatientId);
            entity.HasOne(e => e.Doctor).WithMany().HasForeignKey(e => e.DoctorId);
            entity.HasOne(e => e.SearchSession).WithMany().HasForeignKey(e => e.SearchSessionId);
        });

        modelBuilder.Entity<DoctorLocation>(entity =>
        {
            entity.ToTable("doctor_locations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.Address1).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Address2).HasMaxLength(200);
            entity.Property(e => e.City).HasMaxLength(100).IsRequired();
            entity.Property(e => e.State).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ZipCode).HasMaxLength(20).IsRequired();
            entity.Property(e => e.PhoneNumber).HasMaxLength(30).IsRequired();
            entity.Property(e => e.PhoneExt).HasMaxLength(10);
            entity.Property(e => e.Fax).HasMaxLength(30);
            entity.Property(e => e.ContactPersonName).HasMaxLength(200);
            entity.Property(e => e.AppointmentNotificationEmail).HasMaxLength(200);
            entity.HasIndex(e => new { e.DoctorId, e.SortOrder });
            entity.HasOne(e => e.Doctor).WithMany(d => d.Locations).HasForeignKey(e => e.DoctorId);
        });

        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.ToTable("appointments");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PatientName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.PatientPhone).HasMaxLength(30);
            entity.Property(e => e.PatientEmail).HasMaxLength(200);
            entity.Property(e => e.VisitReason).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(40).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(40).IsRequired();
            entity.HasIndex(e => new { e.DoctorId, e.StartsAt });
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Doctor).WithMany().HasForeignKey(e => e.DoctorId);
            entity.HasOne(e => e.Patient).WithMany().HasForeignKey(e => e.PatientId);
        });

        modelBuilder.Entity<PmsConnection>(entity =>
        {
            entity.ToTable("pms_connections");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(40).IsRequired();
            entity.Property(e => e.DeveloperApiKey).HasMaxLength(500);
            entity.Property(e => e.CustomerApiKey).HasMaxLength(500);
            entity.Property(e => e.ApiKey).HasMaxLength(500);
            entity.Property(e => e.InstitutionId).HasMaxLength(100);
            entity.Property(e => e.LocationExternalId).HasMaxLength(100);
            entity.Property(e => e.ProviderExternalId).HasMaxLength(100);
            entity.Property(e => e.OperatoryId).HasMaxLength(100);
            entity.Property(e => e.ClinicNum).HasMaxLength(50);
            entity.Property(e => e.BaseUrl).HasMaxLength(300);
            entity.Property(e => e.LastError).HasMaxLength(500);
            entity.HasIndex(e => new { e.DoctorId, e.Provider }).IsUnique();
            entity.HasOne(e => e.Doctor).WithMany().HasForeignKey(e => e.DoctorId);
        });

        modelBuilder.Entity<PmsExternalRef>(entity =>
        {
            entity.ToTable("pms_external_refs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(40).IsRequired();
            entity.Property(e => e.ExternalAppointmentId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ExternalPatientId).HasMaxLength(100);
            entity.Property(e => e.ExternalLocationId).HasMaxLength(100);
            entity.Property(e => e.SyncDirection).HasMaxLength(20).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(500);
            entity.HasIndex(e => new { e.Provider, e.ExternalAppointmentId }).IsUnique();
            entity.HasIndex(e => e.AppointmentId);
            entity.HasOne(e => e.Doctor).WithMany().HasForeignKey(e => e.DoctorId);
            entity.HasOne(e => e.Appointment).WithMany().HasForeignKey(e => e.AppointmentId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
