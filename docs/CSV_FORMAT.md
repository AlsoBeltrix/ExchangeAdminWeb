# CSV Bulk Upload Format

## Mailbox Permissions CSV

### Columns
- `Target` - Target mailbox (accepts SMTP, UPN, or SamAccountName)
- `User` - User to grant/revoke permissions (accepts SMTP, UPN, or SamAccountName)
- `FullAccess` - Grant/revoke Full Access permission (Yes/No/True/False/1/0/X)
- `SendAs` - Grant/revoke Send As permission (Yes/No/True/False/1/0/X)
- `AutoMapping` - (Optional) Auto-add to Outlook for Full Access (Yes/No/True/False/1/0/X, defaults to Yes)

### Example - Add Permissions
```csv
Target,User,FullAccess,SendAs,AutoMapping
helpdesk@example.com,jsmith,Yes,Yes,Yes
finance@example.com,jdoe,Yes,No,No
sales@example.com,DOMAIN\bwilson,No,Yes,
```

### Example - Remove Permissions
```csv
Target,User,FullAccess,SendAs
helpdesk@example.com,jsmith,Yes,Yes
finance@example.com,jdoe,Yes,No
```

---

## Calendar Permissions CSV

### Columns (Set Mode)
- `Target` - Target mailbox (accepts SMTP, UPN, or SamAccountName). Calendar folder auto-detected.
- `User` - User to grant access (accepts SMTP, UPN, or SamAccountName)
- `AccessRight` - Permission level (see values below)

### Columns (Remove Mode)
- `Target` - Target mailbox (accepts SMTP, UPN, or SamAccountName). Calendar folder auto-detected.
- `User` - User to revoke access (accepts SMTP, UPN, or SamAccountName)

### AccessRight Values
- `Owner` - Full control
- `PublishingEditor` - Create, read, edit all items
- `Editor` - Create and edit all items
- `PublishingAuthor` - Create, read, edit own items
- `Author` - Create and edit own items
- `NonEditingAuthor` - Create and read all items
- `Contributor` - Create items only
- `Reviewer` - Read all items
- `LimitedDetails` - See free/busy, subject, and location
- `AvailabilityOnly` - See only free/busy time
- `None` - Remove all access

### Example - Set Permissions
```csv
Target,User,AccessRight
exec@example.com,assistant@example.com,Editor
manager@example.com,team@example.com,Reviewer
sales@example.com,finance@example.com,LimitedDetails
```

### Example - Remove Permissions
```csv
Target,User
exec@example.com,oldassistant@example.com
manager@example.com,exemployee@example.com
```

---

## Notes

- **Identity Formats**: All `Target` and `User` fields accept:
  - SMTP address: `user@example.com`
  - UPN: `user@example.onmicrosoft.com`
  - SamAccountName: `DOMAIN\user` or just `user`

- **Calendar Folders**: The app automatically detects the correct calendar folder name for each mailbox (handles international/localized folder names like "Kalender", "Calendrier", etc.)

- **Boolean Values**: For Yes/No fields, accepted values are:
  - **True**: `Yes`, `True`, `1`, `X` (case-insensitive)
  - **False**: `No`, `False`, `0`, or empty

- **File Size Limit**: 10 MB maximum CSV file size

- **Error Handling**: If any row fails, the operation continues. Results show success count, failed count, and error details for each failed row.
