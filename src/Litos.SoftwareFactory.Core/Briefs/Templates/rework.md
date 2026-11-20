# Factory rework brief

The tester asked for changes to this task. You are on the same branch, `{{branch}}`, on project **{{project}}**. Add to the existing work; do not start over.

{{contract}}

## Tester's feedback

{{feedback}}

## What was handed off

{{previousHandoff}}

## Files changed since `{{baseBranch}}`

{{changedFiles}}

{{decisions}}

## Finishing

Address the feedback, update or add unit tests for what you change, and call `submit_work`. Put a rule a page depends on (who may do what, what counts as upcoming) in a plain function, method or hook that the page calls, and unit-test that. Do not add a component-test library (bUnit, React Testing Library, Vue Test Utils) to test a page's markup; use one only if the project already does. If the feedback is ambiguous about a material choice, or addressing it would stop existing data, files or callers from working, call `request_decision` instead.
