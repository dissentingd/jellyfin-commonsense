"""Command-line entry point. Commands are placeholders until v0.1 lands."""

import typer

from jellyfin_commonsense import __version__

app = typer.Typer(help="Re-rate a Jellyfin library to modern parental-rating standards.")


def _not_yet() -> None:
    typer.echo("Not implemented yet - see docs/DESIGN.md.", err=True)
    raise typer.Exit(code=2)


@app.command()
def plan() -> None:
    """Dry run: show which items would get a Custom Rating, and why."""
    _not_yet()


@app.command()
def apply() -> None:
    """Write Custom Ratings and record them in a ledger."""
    _not_yet()


@app.command()
def export() -> None:
    """Snapshot current Custom Ratings to a ledger file."""
    _not_yet()


@app.command()
def restore() -> None:
    """Re-apply a ledger file."""
    _not_yet()


@app.command()
def version() -> None:
    """Print the version."""
    typer.echo(__version__)


def main() -> None:
    app()
