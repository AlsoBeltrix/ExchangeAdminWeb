# CSV Bulk Upload Format

## Mailbox Permissions CSV

### Columns
- `Target` - Target mailbox (accepts SMTP, UPN, or SamAccountName)
- `User` - User to grant/revoke permissions (accepts SMTP, UPN, or SamAccountName)
- `FullAccess` - Grant/revoke Full Access permission (True/False)
- `SendAs` - Grant/revoke Send As permission (True/False)
- `AutoMapping` - (Optional) Auto-add to Outlook for Full Access (True/False, defaults to True)

### Example - Add Permissions
```csv
Target,User,FullAccess,SendAs,AutoMapping
helpdesk@example.com,jsmith,True,True,True
finance@example.com,jdoe,True,False,False
sales@example.com,DOMAIN\bwilson,False,True,
```

### Example - Remove Permissions
```csv
Target,User,FullAccess,SendAs
helpdesk@example.com,jsmith,True,True
finance@example.com,jdoe,True,False
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

- **Boolean Values**: Use `True` or `False` (case-insensitive). Any other value is rejected with an error. Optional boolean fields left blank use their documented default.

- **File Size Limit**: 10 MB maximum CSV file size

- **Error Handling**: If any row fails, the operation continues. Results show success count, failed count, and error details for each failed row.
