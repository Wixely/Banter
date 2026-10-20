"""On a tag, take main's verdict for this commit instead of rolling the dice again.

Three releases in a row (0.13.1, 0.14.0, 0.15.0) were tagged on a commit main had just passed,
and the tag's own run of the same suite failed on a race main's run had happened to win. Each
cost a version number for nothing anybody shipped. The tests ARE the gate - but a commit has one
verdict, and the run that already produced it is the one to read.

Finds this workflow's run for the same commit on the default branch, waits for it to finish, and
writes `tested=true` when it passed. Writes `tested=false` when there is no such run - a tag on a
commit that never went to main - so the caller runs the tests itself, as before. Fails outright
when main's run failed: a release should not be cut from a red commit, and re-running the suite
here until it happens to pass would be exactly the dice this exists to put down.

Environment: GITHUB_REPOSITORY, GITHUB_SHA, GITHUB_RUN_ID, GH_TOKEN, GITHUB_OUTPUT (all set by
Actions), and optionally MAIN_BRANCH (main), APPEAR_SECONDS (120) and FINISH_SECONDS (1800).
"""
import json
import os
import sys
import time
import urllib.request

repo = os.environ["GITHUB_REPOSITORY"]
sha = os.environ["GITHUB_SHA"]
own_run = os.environ["GITHUB_RUN_ID"]
token = os.environ["GH_TOKEN"]
branch = os.environ.get("MAIN_BRANCH", "main")
appear_by = time.monotonic() + float(os.environ.get("APPEAR_SECONDS", "120"))
finish_by = time.monotonic() + float(os.environ.get("FINISH_SECONDS", "1800"))


def api(path):
    request = urllib.request.Request(
        f"https://api.github.com/repos/{repo}/{path}",
        headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
                 "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "banter-ci"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def write_output(tested):
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as out:
        out.write(f"tested={'true' if tested else 'false'}\n")


# The same workflow, by id rather than name: a second workflow in this repository could share
# the name and would not be running these tests.
workflow_id = api(f"actions/runs/{own_run}")["workflow_id"]


def mains_run():
    runs = api(f"actions/runs?head_sha={sha}&branch={branch}&event=push&per_page=20")["workflow_runs"]
    runs = [r for r in runs if r["workflow_id"] == workflow_id and str(r["id"]) != own_run]
    # Newest first, so a re-run of main's workflow outranks the run it replaced.
    runs.sort(key=lambda r: r["run_number"], reverse=True)
    return runs[0] if runs else None


# Both pushes usually arrive together, so main's run exists within seconds; the grace is for the
# API catching up. Past it, this commit was never pushed to main and the suite runs here.
run = mains_run()
while run is None and time.monotonic() < appear_by:
    time.sleep(10)
    run = mains_run()

if run is None:
    print(f"::notice::No run of this workflow for {sha[:7]} on {branch}; running the tests here.")
    write_output(False)
    sys.exit(0)

print(f"{branch} run {run['id']} ({run['html_url']}) for {sha[:7]}: {run['status']}")
while run["status"] != "completed":
    if time.monotonic() >= finish_by:
        print(f"::warning::{branch}'s run {run['id']} has not finished; running the tests here rather than wait longer.")
        write_output(False)
        sys.exit(0)
    time.sleep(20)
    run = api(f"actions/runs/{run['id']}")

if run["conclusion"] == "success":
    print(f"{branch} passed this commit in run {run['id']}; taking that verdict.")
    write_output(True)
    sys.exit(0)

print(f"::error::{branch}'s run {run['id']} for {sha[:7]} concluded '{run['conclusion']}'. "
      f"A release is not cut from a commit {branch} failed; fix {branch} and tag again. {run['html_url']}")
sys.exit(1)
