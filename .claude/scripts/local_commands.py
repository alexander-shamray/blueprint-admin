"""This repository's own commands, as the payload's grant and runbook cases read them through `review_helpers`."""

# Its split commands' runbook ceilings, in bytes with CRLF counted as LF.
CEILINGS = {
    "review-branch.md": 12200,
}

# Its own suites that list the tree, in `suite_select`'s `TREE_READERS` shape: (pattern, (selector, ...)) rows.
TREE_READERS = (
    ("**", ("test_harness_helpers_admin.AdminStack", "test_sweep_slices_admin.TheRealTree")),
    ("**/.gitignore", ("test_root_ignore_coverage.ThisRepository",)),
)
