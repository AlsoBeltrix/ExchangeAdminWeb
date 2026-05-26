# Bulk CSV Performance Test Plan

## Purpose
Validate that bulk CSV operations (mailbox permissions, calendar permissions) use a single pooled Exchange Online connection for the entire batch instead of per-row connections.

## Expected Behavior
- A 20-row CSV should complete in under 60 seconds (previously ~3-5 minutes)
- All rows should execute against the same EXO session
- No "Exchange service is busy" errors during batch processing

---

## Test 1: Bulk Mailbox Permission Add

### Setup
1. Create a test CSV file `tools/test-mailbox-add.csv`:
```csv
Target,User,FullAccess,SendAs,AutoMapping
testmailbox1@analog.com,testuser1@analog.com,true,false,true
testmailbox2@analog.com,testuser1@analog.com,true,false,true
testmailbox3@analog.com,testuser1@analog.com,false,true,false
testmailbox4@analog.com,testuser2@analog.com,true,true,true
testmailbox5@analog.com,testuser2@analog.com,true,false,true
```

### Steps
1. Navigate to Mailbox Permissions page
2. Select "Add Permissions" bulk mode
3. Upload the CSV
4. Enter a valid ticket number
5. Click Submit and start a timer

### Expected Result
- All 5 rows process successfully (or fail with mailbox-not-found, NOT connection errors)
- Total time: under 30 seconds for 5 rows
- No "Exchange service is busy" errors
- Each row shows individual SUCCESS/FAILED status

---

## Test 2: Bulk Mailbox Permission Remove

### Setup
Use the same mailboxes from Test 1 (if they succeeded):
```csv
Target,User,FullAccess,SendAs
testmailbox1@analog.com,testuser1@analog.com,true,false
testmailbox2@analog.com,testuser1@analog.com,true,false
```

### Steps
1. Navigate to Mailbox Permissions page
2. Select "Remove Permissions" bulk mode
3. Upload the CSV
4. Enter a valid ticket number
5. Click Submit and time it

### Expected Result
- Completes in under 20 seconds for 2 rows
- No connection errors

---

## Test 3: Bulk Calendar Permission Set

### Setup
Create `tools/test-calendar-set.csv`:
```csv
Target,User,AccessRight
testmailbox1@analog.com,testuser1@analog.com,Reviewer
testmailbox2@analog.com,testuser1@analog.com,Editor
testmailbox3@analog.com,testuser2@analog.com,Reviewer
```

### Steps
1. Navigate to Calendar Permissions page
2. Select "Set Permissions" bulk mode
3. Upload the CSV
4. Enter a valid ticket number
5. Click Submit and time it

### Expected Result
- All 3 rows process in under 20 seconds
- No connection errors between rows

---

## Test 4: Stress Test (20 rows)

### Setup
Create a 20-row CSV with a mix of valid and invalid targets:
- 15 valid mailboxes
- 5 non-existent mailboxes (to test error handling mid-batch)

### Steps
1. Upload the 20-row CSV to Mailbox Permissions (Add mode)
2. Time the entire operation

### Expected Result
- Total time: under 90 seconds
- Valid rows succeed, invalid rows fail with "mailbox not found" (NOT connection errors)
- The batch does NOT abort on individual row failures

---

## Test 5: Excluded User Protection

### Setup
Add a test user to the ExcludedUsers list in Module Config > Mailbox Permissions

### Steps
1. Create a CSV that includes the excluded user as a Target
2. Upload and process

### Expected Result
- Excluded user row shows "Access denied: protected"
- Other rows in the same CSV still process normally
- No batch-level failure

---

## What to Report
For each test, record:
- Total elapsed time (stopwatch)
- Number of SUCCESS rows
- Number of FAILED rows
- Any error messages that mention "connection", "busy", or "timeout"
- Whether the UI remained responsive during processing (spinner visible)

## Known Baseline (Pre-Fix)
Before the connection reuse optimization, a 20-row CSV took 3-5 minutes because each row opened and closed a separate EXO pool session. The fix borrows one session for the entire batch.
