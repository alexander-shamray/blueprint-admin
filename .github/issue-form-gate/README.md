# The issue-form gate

**The claim: every issue form under `.github/ISSUE_TEMPLATE/` offers exactly
the vocabularies its owner holds, every choice it offers is a label in this
repository, and a blank issue is switched off for a filer below write
access.** The owner is
[`gh-issue-filing.sh`](../../.claude/scripts/gh-issue-filing.sh)'s two closed
`case` arms, the kind's and the severity's, which every filing through
`gh-issue-create.sh` and the sweeps' helper passes. The area choices are the
form's own, and what each covers is its label's description on the
*Labels* page, which the form cites rather than restates.

## What it reads

- `config.yml`, which must set `blank_issues_enabled: false`: with a blank
  issue on offer, a filer can skip the form and every field it asks for.
- Every other `.yml` or `.yaml` in the directory as a form. Each must carry
  a `kind`, a `severity` and an `area` dropdown and no other dropdown, so
  every dropdown option is a label name. An empty directory fails.
- The form's kind against the helper's `case "$kind"` arm, and its severity
  against its `case "$severity"` arm; as sets, with a repeated option
  refused. A word the helper gains or drops fails the form, and the form
  offering a word the helper would refuse fails too.
- Every dropdown option against the names `gh label list --json name`
  prints, on stdin. An empty or malformed list fails, so a gate that read no
  label never passes.

The form is read with a line reader that accepts the shape `finding.yml`
keeps and refuses what it cannot place, because there is no stdlib YAML
parser and a gate needing a `pip install` is a gate that gets skipped: an
options list in flow style, an option that is not a bare label name, a line
inside an options list that is not an option, options under a field that is
not a dropdown, a dropdown with no id or no options, or the same dropdown
twice.

## What it does not claim

- A form attaches no label. The answers sit in the issue body under the
  dropdowns' headings, and whoever triages copies them onto the issue; the
  gate holds that each answer has a label to copy onto.
- It does not read the text fields, the label colours or descriptions,
  or whether GitHub accepts the form: GitHub validates the schema when it
  renders the form, and a broken one shows on the *New issue* page.
- The gate reads label names, not descriptions, so it holds the area
  choices to the labels and says nothing about what each covers.
- The form offers only the helper's kinds. A filing of another kind, an
  `enhancement` say, has no form; with blank issues off, a filer below write
  access cannot make one from the web UI.
- A filer with write access still sees GitHub's *Blank issue*, marked
  *Maintainers only*: the setting binds the read and triage roles, and
  nothing in a repository's files can switch it off for the rest.

## How it runs

Its suite is a root of the harness suite, so the `harness` job and
`harness-checks.sh` run it. The gate itself runs on the `harness` job's
Linux leg in [`ci.yml`](../workflows/ci.yml), which reads the labels with the
job's token; the suite holds that wiring. Locally:

```bash
py -3.12 -m unittest discover -s .github/issue-form-gate
gh label list --limit 1000 --json name | py -3.12 .github/issue-form-gate/issue_form_gate.py
```
