using Docovee.DS;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Data;

/// <summary>
/// Applies schema changes for databases created before newer entities/columns were added.
/// EnsureCreated does not update an existing database.
/// </summary>
public static class SchemaUpdater
{
    public static async Task EnsureLatestSchemaAsync(DocoveeDbContext db, CancellationToken cancellationToken = default)
    {
        Log("Checking MySQL connection…");
        if (!await db.Database.CanConnectAsync(cancellationToken))
            throw new InvalidOperationException("Cannot connect to MySQL. Start MySQL and verify ConnectionStrings:DefaultConnection.");
        Log("MySQL connected.");

        if (!await HasCoreTablesAsync(db, cancellationToken))
        {
            Log("Empty database — creating initial schema (one-time)…");
            await db.Database.EnsureCreatedAsync(cancellationToken);
            Log("Initial schema created.");
        }
        else
        {
            Log("Existing database — applying incremental schema updates…");
        }

        Log("Ensuring admin tables…");
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `admins` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `Username` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
                `PasswordHash` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
                `CreatedAt` datetime(6) NOT NULL,
                PRIMARY KEY (`Id`),
                UNIQUE KEY `IX_admins_Username` (`Username`)
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `app_settings` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `Key` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
                `Value` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
                `UpdatedAt` datetime(6) NOT NULL,
                PRIMARY KEY (`Id`),
                UNIQUE KEY `IX_app_settings_Key` (`Key`)
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `doctor_patient_reviews` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `DoctorId` int NOT NULL,
                `PatientId` int NULL,
                `ReviewerName` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
                `Rating` int NOT NULL,
                `ReviewText` text CHARACTER SET utf8mb4 NOT NULL,
                `CreatedAt` datetime(6) NOT NULL,
                PRIMARY KEY (`Id`),
                KEY `IX_doctor_patient_reviews_DoctorId` (`DoctorId`),
                KEY `IX_doctor_patient_reviews_PatientId` (`PatientId`),
                CONSTRAINT `FK_doctor_patient_reviews_doctors_DoctorId` FOREIGN KEY (`DoctorId`) REFERENCES `doctors` (`Id`) ON DELETE CASCADE,
                CONSTRAINT `FK_doctor_patient_reviews_patients_PatientId` FOREIGN KEY (`PatientId`) REFERENCES `patients` (`Id`) ON DELETE SET NULL
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `polling_questions` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `Question` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
                `ValidationHint` varchar(500) CHARACTER SET utf8mb4 NULL,
                `SortOrder` int NOT NULL,
                `IsActive` tinyint(1) NOT NULL,
                `CreatedAt` datetime(6) NOT NULL,
                PRIMARY KEY (`Id`)
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `doctor_languages` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `Name` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
                `SortOrder` int NOT NULL,
                `IsActive` tinyint(1) NOT NULL,
                `CreatedAt` datetime(6) NOT NULL,
                PRIMARY KEY (`Id`),
                UNIQUE KEY `IX_doctor_languages_Name` (`Name`)
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `doctor_doctor_languages` (
                `DoctorId` int NOT NULL,
                `DoctorLanguageId` int NOT NULL,
                PRIMARY KEY (`DoctorId`, `DoctorLanguageId`),
                KEY `IX_doctor_doctor_languages_DoctorLanguageId` (`DoctorLanguageId`),
                CONSTRAINT `FK_doctor_doctor_languages_doctors_DoctorId` FOREIGN KEY (`DoctorId`) REFERENCES `doctors` (`Id`) ON DELETE CASCADE,
                CONSTRAINT `FK_doctor_doctor_languages_doctor_languages_DoctorLanguageId` FOREIGN KEY (`DoctorLanguageId`) REFERENCES `doctor_languages` (`Id`) ON DELETE CASCADE
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `patient_doctor_contact_views` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `PatientId` int NOT NULL,
                `DoctorId` int NOT NULL,
                `SearchSessionId` int NULL,
                `ViewedAt` datetime(6) NOT NULL,
                PRIMARY KEY (`Id`),
                UNIQUE KEY `IX_patient_doctor_contact_views_PatientId_DoctorId` (`PatientId`, `DoctorId`),
                KEY `IX_patient_doctor_contact_views_DoctorId` (`DoctorId`),
                KEY `IX_patient_doctor_contact_views_SearchSessionId` (`SearchSessionId`),
                CONSTRAINT `FK_patient_doctor_contact_views_patients_PatientId` FOREIGN KEY (`PatientId`) REFERENCES `patients` (`Id`) ON DELETE CASCADE,
                CONSTRAINT `FK_patient_doctor_contact_views_doctors_DoctorId` FOREIGN KEY (`DoctorId`) REFERENCES `doctors` (`Id`) ON DELETE CASCADE,
                CONSTRAINT `FK_patient_doctor_contact_views_search_sessions_SearchSessionId` FOREIGN KEY (`SearchSessionId`) REFERENCES `search_sessions` (`Id`) ON DELETE SET NULL
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await EnsureColumnAsync(db, "polling_questions", "MatchWeight", "int NOT NULL DEFAULT 5", cancellationToken);
        await EnsureColumnAsync(db, "polling_questions", "MatchWeightLabel", "varchar(50) NULL", cancellationToken);

        await EnsureColumnAsync(db, "search_sessions", "MedicalIssuesSummary", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "search_sessions", "SearchContextJson", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "search_sessions", "InsurancePlanText", "varchar(200) NULL", cancellationToken);

        await EnsureColumnAsync(db, "doctors", "Location", "varchar(200) NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "PracticeName", "varchar(200) NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "Address", "varchar(500) NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "OfficePhoneNumber", "varchar(30) NULL", cancellationToken);
        await EnsureTextColumnAsync(db, "doctors", "PhotoUrl", cancellationToken);
        await EnsureTextColumnAsync(db, "doctors", "GmbPhotoLink", cancellationToken);
        await EnsureTextColumnAsync(db, "doctors", "VideoUrl", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "SummaryOfReviews", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "Top3Procedures", "varchar(500) NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "Niche", "varchar(200) NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "OffersDentalImplants", "tinyint(1) NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "OffersTmj", "tinyint(1) NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "OffersBotox", "tinyint(1) NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "Age", "int NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "YearsOfPractice", "int NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "ProcedureCount", "int NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "GraduationYear", "int NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "PracticeCount", "int NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "Username", "varchar(100) NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "PasswordHash", "varchar(500) NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "OnboardingProfileJson", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "OnboardingQuestionIndex", "int NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "ProfileCompletionPercent", "int NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "patients", "PreferenceProfileJson", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "appointments", "PatientDateOfBirth", "date NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctor_patient_reviews", "WaitingTime", "varchar(50) NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctor_patient_reviews", "Recommendation", "varchar(50) NULL", cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `doctor_onboarding_sessions` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `SessionKey` char(36) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL,
                `ContextJson` text CHARACTER SET utf8mb4 NOT NULL,
                `DoctorId` int NULL,
                `CreatedAt` datetime(6) NOT NULL,
                `UpdatedAt` datetime(6) NOT NULL,
                PRIMARY KEY (`Id`),
                UNIQUE KEY `IX_doctor_onboarding_sessions_SessionKey` (`SessionKey`)
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `appointments` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `DoctorId` int NOT NULL,
                `PatientId` int NULL,
                `PatientName` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
                `PatientPhone` varchar(30) CHARACTER SET utf8mb4 NULL,
                `PatientEmail` varchar(200) CHARACTER SET utf8mb4 NULL,
                `VisitReason` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
                `StartsAt` datetime(6) NOT NULL,
                `Status` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
                `Source` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
                `SearchSessionId` int NULL,
                `CreatedAt` datetime(6) NOT NULL,
                `UpdatedAt` datetime(6) NOT NULL,
                PRIMARY KEY (`Id`),
                KEY `IX_appointments_DoctorId_StartsAt` (`DoctorId`, `StartsAt`),
                KEY `IX_appointments_Status` (`Status`),
                KEY `IX_appointments_PatientId` (`PatientId`),
                CONSTRAINT `FK_appointments_doctors_DoctorId` FOREIGN KEY (`DoctorId`) REFERENCES `doctors` (`Id`) ON DELETE CASCADE,
                CONSTRAINT `FK_appointments_patients_PatientId` FOREIGN KEY (`PatientId`) REFERENCES `patients` (`Id`) ON DELETE SET NULL
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `doctor_locations` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `DoctorId` int NOT NULL,
                `Name` varchar(200) CHARACTER SET utf8mb4 NULL,
                `InPerson` tinyint(1) NOT NULL DEFAULT 1,
                `VideoVisits` tinyint(1) NOT NULL DEFAULT 0,
                `Address1` varchar(300) CHARACTER SET utf8mb4 NOT NULL,
                `Address2` varchar(200) CHARACTER SET utf8mb4 NULL,
                `City` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
                `State` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
                `ZipCode` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
                `PhoneNumber` varchar(30) CHARACTER SET utf8mb4 NOT NULL,
                `PhoneExt` varchar(10) CHARACTER SET utf8mb4 NULL,
                `Fax` varchar(30) CHARACTER SET utf8mb4 NULL,
                `ContactPersonName` varchar(200) CHARACTER SET utf8mb4 NULL,
                `AppointmentNotificationEmail` varchar(200) CHARACTER SET utf8mb4 NULL,
                `IsActive` tinyint(1) NOT NULL DEFAULT 1,
                `IsPrimary` tinyint(1) NOT NULL DEFAULT 0,
                `SortOrder` int NOT NULL DEFAULT 0,
                `CreatedAt` datetime(6) NOT NULL,
                `UpdatedAt` datetime(6) NOT NULL,
                PRIMARY KEY (`Id`),
                KEY `IX_doctor_locations_DoctorId_SortOrder` (`DoctorId`, `SortOrder`),
                CONSTRAINT `FK_doctor_locations_doctors_DoctorId` FOREIGN KEY (`DoctorId`) REFERENCES `doctors` (`Id`) ON DELETE CASCADE
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `insurance_plans` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `InsuranceCarrierId` int NOT NULL,
                `Name` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
                `IsActive` tinyint(1) NOT NULL,
                `SortOrder` int NOT NULL,
                PRIMARY KEY (`Id`),
                UNIQUE KEY `IX_insurance_plans_Carrier_Name` (`InsuranceCarrierId`, `Name`),
                KEY `IX_insurance_plans_InsuranceCarrierId` (`InsuranceCarrierId`),
                CONSTRAINT `FK_insurance_plans_insurance_carriers_InsuranceCarrierId`
                    FOREIGN KEY (`InsuranceCarrierId`) REFERENCES `insurance_carriers` (`Id`) ON DELETE CASCADE
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `pms_connections` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `DoctorId` int NOT NULL,
                `Provider` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
                `IsEnabled` tinyint(1) NOT NULL DEFAULT 0,
                `DeveloperApiKey` varchar(500) CHARACTER SET utf8mb4 NULL,
                `CustomerApiKey` varchar(500) CHARACTER SET utf8mb4 NULL,
                `ApiKey` varchar(500) CHARACTER SET utf8mb4 NULL,
                `InstitutionId` varchar(100) CHARACTER SET utf8mb4 NULL,
                `LocationExternalId` varchar(100) CHARACTER SET utf8mb4 NULL,
                `ProviderExternalId` varchar(100) CHARACTER SET utf8mb4 NULL,
                `OperatoryId` varchar(100) CHARACTER SET utf8mb4 NULL,
                `ClinicNum` varchar(50) CHARACTER SET utf8mb4 NULL,
                `BaseUrl` varchar(300) CHARACTER SET utf8mb4 NULL,
                `LastError` varchar(500) CHARACTER SET utf8mb4 NULL,
                `LastSyncAt` datetime(6) NULL,
                `LastTestAt` datetime(6) NULL,
                `CreatedAt` datetime(6) NOT NULL,
                `UpdatedAt` datetime(6) NOT NULL,
                PRIMARY KEY (`Id`),
                UNIQUE KEY `IX_pms_connections_DoctorId_Provider` (`DoctorId`, `Provider`),
                KEY `IX_pms_connections_DoctorId` (`DoctorId`),
                CONSTRAINT `FK_pms_connections_doctors_DoctorId` FOREIGN KEY (`DoctorId`) REFERENCES `doctors` (`Id`) ON DELETE CASCADE
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `pms_external_refs` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `DoctorId` int NOT NULL,
                `AppointmentId` int NULL,
                `Provider` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
                `ExternalAppointmentId` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
                `ExternalPatientId` varchar(100) CHARACTER SET utf8mb4 NULL,
                `ExternalLocationId` varchar(100) CHARACTER SET utf8mb4 NULL,
                `SyncDirection` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
                `LastError` varchar(500) CHARACTER SET utf8mb4 NULL,
                `CreatedAt` datetime(6) NOT NULL,
                `UpdatedAt` datetime(6) NOT NULL,
                PRIMARY KEY (`Id`),
                UNIQUE KEY `IX_pms_external_refs_Provider_ExternalAppointmentId` (`Provider`, `ExternalAppointmentId`),
                KEY `IX_pms_external_refs_AppointmentId` (`AppointmentId`),
                KEY `IX_pms_external_refs_DoctorId` (`DoctorId`),
                CONSTRAINT `FK_pms_external_refs_doctors_DoctorId` FOREIGN KEY (`DoctorId`) REFERENCES `doctors` (`Id`) ON DELETE CASCADE,
                CONSTRAINT `FK_pms_external_refs_appointments_AppointmentId` FOREIGN KEY (`AppointmentId`) REFERENCES `appointments` (`Id`) ON DELETE SET NULL
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `audit_trail` (
                `Id` bigint NOT NULL AUTO_INCREMENT,
                `OccurredAtUtc` datetime(6) NOT NULL,
                `Action` varchar(40) CHARACTER SET utf8mb4 NOT NULL,
                `EntityType` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
                `EntityId` varchar(100) CHARACTER SET utf8mb4 NULL,
                `ActorUserId` varchar(50) CHARACTER SET utf8mb4 NULL,
                `ActorUsername` varchar(200) CHARACTER SET utf8mb4 NULL,
                `ActorRole` varchar(40) CHARACTER SET utf8mb4 NULL,
                `IpAddress` varchar(64) CHARACTER SET utf8mb4 NULL,
                `UserAgent` varchar(500) CHARACTER SET utf8mb4 NULL,
                `Success` tinyint(1) NOT NULL DEFAULT 1,
                `ErrorMessage` varchar(1000) CHARACTER SET utf8mb4 NULL,
                `Summary` varchar(500) CHARACTER SET utf8mb4 NULL,
                `OldValuesJson` text CHARACTER SET utf8mb4 NULL,
                `NewValuesJson` text CHARACTER SET utf8mb4 NULL,
                PRIMARY KEY (`Id`),
                KEY `IX_audit_trail_OccurredAtUtc` (`OccurredAtUtc`),
                KEY `IX_audit_trail_EntityType_EntityId` (`EntityType`, `EntityId`),
                KEY `IX_audit_trail_ActorUserId` (`ActorUserId`),
                KEY `IX_audit_trail_Action` (`Action`)
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS `patient_insurance_coverages` (
                `Id` int NOT NULL AUTO_INCREMENT,
                `PatientId` int NOT NULL,
                `InsuranceType` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
                `InsuranceCarrierId` int NULL,
                `InsurancePlanId` int NULL,
                `CustomCarrierName` varchar(200) CHARACTER SET utf8mb4 NULL,
                `CustomPlanName` varchar(200) CHARACTER SET utf8mb4 NULL,
                `MemberId` varchar(100) CHARACTER SET utf8mb4 NULL,
                `CardPhotoUrl` varchar(500) CHARACTER SET utf8mb4 NULL,
                `UpdatedAt` datetime(6) NOT NULL,
                PRIMARY KEY (`Id`),
                UNIQUE KEY `IX_patient_insurance_coverages_Patient_Type` (`PatientId`, `InsuranceType`),
                KEY `IX_patient_insurance_coverages_InsuranceCarrierId` (`InsuranceCarrierId`),
                KEY `IX_patient_insurance_coverages_InsurancePlanId` (`InsurancePlanId`),
                CONSTRAINT `FK_patient_insurance_coverages_patients_PatientId`
                    FOREIGN KEY (`PatientId`) REFERENCES `patients` (`Id`) ON DELETE CASCADE,
                CONSTRAINT `FK_patient_insurance_coverages_carriers_InsuranceCarrierId`
                    FOREIGN KEY (`InsuranceCarrierId`) REFERENCES `insurance_carriers` (`Id`) ON DELETE SET NULL,
                CONSTRAINT `FK_patient_insurance_coverages_plans_InsurancePlanId`
                    FOREIGN KEY (`InsurancePlanId`) REFERENCES `insurance_plans` (`Id`) ON DELETE SET NULL
            ) CHARACTER SET=utf8mb4;
            """, cancellationToken);

        await EnsureColumnAsync(db, "patients", "IdCardPhotoUrl", "varchar(500) NULL", cancellationToken);
        await EnsureColumnAsync(db, "patients", "HipaaDataSharingOptIn", "tinyint(1) NULL", cancellationToken);
        await EnsureColumnAsync(db, "patients", "CookieTrackingOptOut", "tinyint(1) NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "patients", "AutofillEnabled", "tinyint(1) NOT NULL DEFAULT 0", cancellationToken);

        Log("Schema updates complete.");
    }

    private static void Log(string message) =>
        Console.WriteLine($"[NuviDoc DB] {message}");

    private static async Task<bool> HasCoreTablesAsync(DocoveeDbContext db, CancellationToken cancellationToken)
    {
        var count = await db.Database
            .SqlQueryRaw<int>(
                """
                SELECT COUNT(*) AS Value
                FROM information_schema.tables
                WHERE table_schema = DATABASE()
                  AND table_name = 'doctors'
                """)
            .FirstOrDefaultAsync(cancellationToken);
        return count > 0;
    }

    private static async Task EnsureColumnAsync(
        DocoveeDbContext db,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SET @sql = IF(
                (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                 WHERE TABLE_SCHEMA = DATABASE()
                   AND TABLE_NAME = '{tableName}'
                   AND COLUMN_NAME = '{columnName}') = 0,
                'ALTER TABLE `{tableName}` ADD `{columnName}` {columnDefinition}',
                'SELECT 1');
            PREPARE stmt FROM @sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            """;

        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureTextColumnAsync(
        DocoveeDbContext db,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(db, tableName, columnName, "TEXT NULL", cancellationToken);

        var modifySql = $"""
            SET @sql = IF(
                (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                 WHERE TABLE_SCHEMA = DATABASE()
                   AND TABLE_NAME = '{tableName}'
                   AND COLUMN_NAME = '{columnName}') > 0,
                'ALTER TABLE `{tableName}` MODIFY COLUMN `{columnName}` TEXT NULL',
                'SELECT 1');
            PREPARE stmt FROM @sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            """;

        await db.Database.ExecuteSqlRawAsync(modifySql, cancellationToken);
    }
}
