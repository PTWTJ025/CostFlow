-- ============================================================
-- CostFlow — Initialize Database Tables (MySQL / TiDB)
-- สคริปต์สร้างตารางทั้งหมดของระบบ (Identity + App Tables + TiDb Tables)
-- สามารถรันซ้ำได้ปลอดภัย (มี IF NOT EXISTS)
-- ============================================================

-- ------------------------------------------------------------
-- 1. ASP.NET Core Identity Tables
-- ------------------------------------------------------------

CREATE TABLE IF NOT EXISTS `AspNetRoles` (
    `Id` varchar(255) NOT NULL,
    `Name` varchar(256) DEFAULT NULL,
    `NormalizedName` varchar(256) DEFAULT NULL,
    `ConcurrencyStamp` longtext DEFAULT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `RoleNameIndex` (`NormalizedName`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `AspNetUsers` (
    `Id` varchar(255) NOT NULL,
    `EmployeeCode` varchar(255) NOT NULL DEFAULT '',
    `FullName` longtext NOT NULL,
    `ProfilePictureUrl` longtext DEFAULT NULL,
    `IsActive` tinyint(1) NOT NULL DEFAULT 1,
    `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    `UserName` varchar(256) DEFAULT NULL,
    `NormalizedUserName` varchar(256) DEFAULT NULL,
    `Email` varchar(256) DEFAULT NULL,
    `NormalizedEmail` varchar(256) DEFAULT NULL,
    `EmailConfirmed` tinyint(1) NOT NULL DEFAULT 0,
    `PasswordHash` longtext DEFAULT NULL,
    `SecurityStamp` longtext DEFAULT NULL,
    `ConcurrencyStamp` longtext DEFAULT NULL,
    `PhoneNumber` longtext DEFAULT NULL,
    `PhoneNumberConfirmed` tinyint(1) NOT NULL DEFAULT 0,
    `TwoFactorEnabled` tinyint(1) NOT NULL DEFAULT 0,
    `LockoutEnd` datetime(6) DEFAULT NULL,
    `LockoutEnabled` tinyint(1) NOT NULL DEFAULT 0,
    `AccessFailedCount` int NOT NULL DEFAULT 0,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `UserNameIndex` (`NormalizedUserName`),
    UNIQUE KEY `IX_AspNetUsers_EmployeeCode` (`EmployeeCode`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `AspNetRoleClaims` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `RoleId` varchar(255) NOT NULL,
    `ClaimType` longtext DEFAULT NULL,
    `ClaimValue` longtext DEFAULT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_AspNetRoleClaims_RoleId` (`RoleId`),
    CONSTRAINT `FK_AspNetRoleClaims_AspNetRoles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `AspNetRoles` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `AspNetUserClaims` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `UserId` varchar(255) NOT NULL,
    `ClaimType` longtext DEFAULT NULL,
    `ClaimValue` longtext DEFAULT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_AspNetUserClaims_UserId` (`UserId`),
    CONSTRAINT `FK_AspNetUserClaims_AspNetUsers_UserId` FOREIGN KEY (`UserId`) REFERENCES `AspNetUsers` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `AspNetUserLogins` (
    `LoginProvider` varchar(255) NOT NULL,
    `ProviderKey` varchar(255) NOT NULL,
    `ProviderDisplayName` longtext DEFAULT NULL,
    `UserId` varchar(255) NOT NULL,
    PRIMARY KEY (`LoginProvider`, `ProviderKey`),
    KEY `IX_AspNetUserLogins_UserId` (`UserId`),
    CONSTRAINT `FK_AspNetUserLogins_AspNetUsers_UserId` FOREIGN KEY (`UserId`) REFERENCES `AspNetUsers` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `AspNetUserRoles` (
    `UserId` varchar(255) NOT NULL,
    `RoleId` varchar(255) NOT NULL,
    PRIMARY KEY (`UserId`, `RoleId`),
    KEY `IX_AspNetUserRoles_RoleId` (`RoleId`),
    CONSTRAINT `FK_AspNetUserRoles_AspNetRoles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `AspNetRoles` (`Id`) ON DELETE CASCADE,
    CONSTRAINT `FK_AspNetUserRoles_AspNetUsers_UserId` FOREIGN KEY (`UserId`) REFERENCES `AspNetUsers` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `AspNetUserTokens` (
    `UserId` varchar(255) NOT NULL,
    `LoginProvider` varchar(255) NOT NULL,
    `Name` varchar(255) NOT NULL,
    `Value` longtext DEFAULT NULL,
    PRIMARY KEY (`UserId`, `LoginProvider`, `Name`),
    CONSTRAINT `FK_AspNetUserTokens_AspNetUsers_UserId` FOREIGN KEY (`UserId`) REFERENCES `AspNetUsers` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ------------------------------------------------------------
-- 2. Business Tables (AppDbContext)
-- ------------------------------------------------------------

CREATE TABLE IF NOT EXISTS `ProductPrices` (
    `ProductCode` varchar(255) NOT NULL,
    `ProductName` longtext NOT NULL,
    `Unit` longtext NOT NULL,
    `TotalQty` double NOT NULL,
    `TotalValue` double NOT NULL,
    `PricePerUnit` double NOT NULL,
    `Sources` longtext NOT NULL,
    PRIMARY KEY (`ProductCode`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `Reports` (
    `Id` char(36) NOT NULL,
    `ReportName` varchar(255) NOT NULL,
    `OriginalFileName` longtext NOT NULL,
    `TotalPOs` int NOT NULL,
    `MatchedPOs` int NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `CreatedBy` longtext DEFAULT NULL,
    `CreatedByUserId` longtext DEFAULT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_Reports_ReportName` (`ReportName`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `OrderTrackingMasters` (
    `Id` char(36) NOT NULL,
    `ReportId` char(36) NOT NULL,
    `PoNumber` longtext NOT NULL,
    `RequestDate` longtext DEFAULT NULL,
    `ApprovedDate` longtext DEFAULT NULL,
    `Urgency` longtext DEFAULT NULL,
    `Amount` longtext DEFAULT NULL,
    `Remarks` longtext DEFAULT NULL,
    `RemarksQuantity` longtext DEFAULT NULL,
    `Status` longtext NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_OrderTrackingMasters_ReportId` (`ReportId`),
    CONSTRAINT `FK_OrderTrackingMasters_Reports_ReportId` FOREIGN KEY (`ReportId`) REFERENCES `Reports` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `WeeklyPlans` (
    `Id` char(36) NOT NULL,
    `ReportId` char(36) NOT NULL,
    `FileName` longtext NOT NULL,
    `SheetName` longtext NOT NULL,
    `TotalRecords` int NOT NULL,
    `MatchedCount` int NOT NULL,
    `UploadedAt` datetime(6) NOT NULL,
    `UploadedBy` longtext DEFAULT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_WeeklyPlans_ReportId` (`ReportId`),
    CONSTRAINT `FK_WeeklyPlans_Reports_ReportId` FOREIGN KEY (`ReportId`) REFERENCES `Reports` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `WeeklyPlanDetails` (
    `Id` char(36) NOT NULL,
    `WeeklyPlanId` char(36) NOT NULL,
    `PoNumberInFile` longtext DEFAULT NULL,
    `Department` longtext DEFAULT NULL,
    `OrderName` longtext DEFAULT NULL,
    `OrderStatus` longtext DEFAULT NULL,
    `DeliveryTarget` longtext DEFAULT NULL,
    `Price` longtext DEFAULT NULL,
    `RowIndex` int NOT NULL,
    `IsMatched` tinyint(1) NOT NULL,
    `MatchedOrderId` char(36) DEFAULT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_WeeklyPlanDetails_MatchedOrderId` (`MatchedOrderId`),
    KEY `IX_WeeklyPlanDetails_WeeklyPlanId` (`WeeklyPlanId`),
    CONSTRAINT `FK_WeeklyPlanDetails_OrderTrackingMasters_MatchedOrderId` FOREIGN KEY (`MatchedOrderId`) REFERENCES `OrderTrackingMasters` (`Id`) ON DELETE SET NULL,
    CONSTRAINT `FK_WeeklyPlanDetails_WeeklyPlans_WeeklyPlanId` FOREIGN KEY (`WeeklyPlanId`) REFERENCES `WeeklyPlans` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `MonthlyOrderActions` (
    `Id` char(36) NOT NULL,
    `OrderTrackingMasterId` char(36) NOT NULL,
    `MonthYear` longtext NOT NULL,
    `Action` longtext NOT NULL,
    `ActionPrice` decimal(65,30) NOT NULL,
    `DeferredFromMonth` longtext DEFAULT NULL,
    `IsForcedPayment` tinyint(1) NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_MonthlyOrderActions_OrderTrackingMasterId` (`OrderTrackingMasterId`),
    CONSTRAINT `FK_MonthlyOrderActions_OrderTrackingMasters_OrderTrackingMasterId` FOREIGN KEY (`OrderTrackingMasterId`) REFERENCES `OrderTrackingMasters` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ------------------------------------------------------------
-- 3. TiDbContext Tables
-- ------------------------------------------------------------

CREATE TABLE IF NOT EXISTS `SavedOrderBatches` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `BatchName` varchar(255) NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `TotalItems` int NOT NULL,
    `TotalAmount` decimal(65,30) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_SavedOrderBatches_BatchName` (`BatchName`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `SavedOrderItems` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `BatchId` int NOT NULL,
    `ProductCode` varchar(100) NOT NULL DEFAULT '',
    `ProductName` varchar(255) NOT NULL DEFAULT '',
    `Unit` varchar(50) NOT NULL DEFAULT '',
    `UnitPrice` decimal(65,30) NOT NULL,
    `Quantity` decimal(65,30) NOT NULL,
    `Remarks` longtext NOT NULL,
    `IsReceived` tinyint(1) NOT NULL DEFAULT 0,
    `ReceiveDate` datetime(6) DEFAULT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_SavedOrderItems_BatchId` (`BatchId`),
    CONSTRAINT `FK_SavedOrderItems_SavedOrderBatches_BatchId` FOREIGN KEY (`BatchId`) REFERENCES `SavedOrderBatches` (`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
