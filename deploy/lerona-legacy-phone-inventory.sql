-- Read-only Lerona inventory. Run against the Lerona database with a read-only role.
-- Save results in restricted operational storage, never in source control or public logs.
-- Before RequireVerifiedPhoneAuthentication is applied, treat all historical accounts as
-- unverified and inventory Id/PhoneNumber presence with the first query only.
SELECT [Id], [ArchivedAtUtc],
       CASE WHEN [PhoneNumber] IS NULL THEN 'missing_phone' ELSE 'phone_requires_review' END AS [ReviewReason]
FROM [Users]
ORDER BY [Id];

-- After the verified-phone migration: affected IDs without exposing contact information.
SELECT [Id],
       CASE WHEN [PhoneNumber] IS NULL THEN 'missing_phone'
            WHEN [PhoneNumber] NOT LIKE '+989[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]' THEN 'noncanonical_phone'
            WHEN [PhoneVerifiedAtUtc] IS NULL THEN 'no_sms_ownership_evidence'
            WHEN [VerifiedPhoneNumber] IS NULL OR [VerifiedPhoneNumber] <> [PhoneNumber] THEN 'ownership_mismatch'
            ELSE 'review' END AS [RecoveryReason]
FROM [Users]
WHERE [ArchivedAtUtc] IS NULL AND
      ([PhoneNumber] IS NULL OR [PhoneVerifiedAtUtc] IS NULL OR [VerifiedPhoneNumber] IS NULL OR
       [VerifiedPhoneNumber] <> [PhoneNumber] OR
       [PhoneNumber] NOT LIKE '+989[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]')
ORDER BY [Id];

-- Detect legacy normalization collisions without merging or changing either account.
SELECT CASE WHEN [PhoneNumber] LIKE '09%' AND LEN([PhoneNumber]) = 11
            THEN '+98' + SUBSTRING([PhoneNumber], 2, 10) ELSE [PhoneNumber] END AS [CanonicalPhone], COUNT(*) AS [Accounts]
FROM [Users] WHERE [PhoneNumber] IS NOT NULL
GROUP BY CASE WHEN [PhoneNumber] LIKE '09%' AND LEN([PhoneNumber]) = 11
              THEN '+98' + SUBSTRING([PhoneNumber], 2, 10) ELSE [PhoneNumber] END
HAVING COUNT(*) > 1;
