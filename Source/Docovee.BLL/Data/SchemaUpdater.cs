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
        await db.Database.EnsureCreatedAsync(cancellationToken);

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

        await EnsureColumnAsync(db, "search_sessions", "MedicalIssuesSummary", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "search_sessions", "SearchContextJson", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "search_sessions", "InsurancePlanText", "varchar(200) NULL", cancellationToken);

        await EnsureColumnAsync(db, "doctors", "Location", "varchar(200) NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "PracticeName", "varchar(200) NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "Address", "varchar(500) NULL", cancellationToken);
        await EnsureColumnAsync(db, "doctors", "OfficePhoneNumber", "varchar(30) NULL", cancellationToken);
        await EnsureTextColumnAsync(db, "doctors", "PhotoUrl", cancellationToken);
        await EnsureTextColumnAsync(db, "doctors", "GmbPhotoLink", cancellationToken);
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
