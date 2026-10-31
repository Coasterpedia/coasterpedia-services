// What a key is for: the kind of project, where it runs, its link and
// whether it makes money. Asked on a new key and on its details page, so a
// reviewer has something to hold its usage against (accounts.tsx), and a
// showcase listing starts from the same answers (listings.tsx).
import { COMMERCIAL, FIX, httpsUrl, KINDS, kindOf, listField, PLATFORMS, platformsOf } from './data';

export interface Project {
	kind: string;
	platforms: string[];
	link: string;
	/** Ticked "It isn't public yet": a public kind with no link. */
	notPublic: boolean;
	commercial: string;
}

export const emptyProject: Project = { kind: '', platforms: [], link: '', notPublic: false, commercial: '' };

export function projectOf(row: { kind: string | null; platforms: string | null; link: string | null; commercial: string | null }): Project {
	return {
		kind: row.kind ?? '',
		platforms: platformsOf(row.platforms),
		link: row.link ?? '',
		notPublic: kindOf(row.kind)?.public === true && !row.link,
		commercial: row.commercial ?? '',
	};
}

/** The form's answers (parsed with all: true), and what to fix. `link` comes back normalised, or empty. */
export function readProject(form: Record<string, unknown>): { project: Project; errors: string[] } {
	const project: Project = {
		kind: String(form.kind ?? ''),
		platforms: platformsOf(listField(form.platforms)),
		link: String(form.link ?? '').trim(),
		notPublic: form.not_public === '1',
		commercial: String(form.commercial ?? ''),
	};
	const errors: string[] = [];
	const kind = kindOf(project.kind);
	if (!kind) errors.push(FIX.kind);
	if (kind?.public && project.platforms.length === 0) errors.push(FIX.keyPlatform);
	if (project.link) {
		const link = httpsUrl(project.link);
		if (link) project.link = link;
		else errors.push(kind?.public ? FIX.keyLink : FIX.optionalLink);
	} else if (kind?.public && !project.notPublic) {
		errors.push(FIX.keyLink);
	}
	if (!COMMERCIAL.some((c) => c.slug === project.commercial)) errors.push(FIX.commercial);
	return { project, errors };
}

/** Where it runs: any number of PLATFORMS, as a list of checkboxes. */
export function PlatformChoices({ checked, legend, help }: { checked: string[]; legend: string; help: string }) {
	return (
		<fieldset class="field">
			<legend class="field__label">{legend}</legend>
			<div class="choices">
				{PLATFORMS.map((p) => (
					<label class="choices__option">
						<input type="checkbox" name="platforms" value={p.slug} checked={checked.includes(p.slug)} />
						<span>{p.label}</span>
					</label>
				))}
			</div>
			<p class="field__help">{help}</p>
		</fieldset>
	);
}

export function ProjectFields({ project }: { project: Project }) {
	return (
		<>
			<fieldset class="field">
				<legend class="field__label">What kind of project is it?</legend>
				<div class="choices">
					{KINDS.map((k) => (
						<label class="choices__option">
							<input type="radio" name="kind" value={k.slug} checked={project.kind === k.slug} required />
							<span>{k.label}</span>
						</label>
					))}
				</div>
			</fieldset>
			<PlatformChoices
				checked={project.platforms}
				legend="Where does it run?"
				help="Choose every one that applies. We need at least one for a project other people use. If you list it in the showcase, we’ll fill these in for you."
			/>
			<div class="field">
				<label class="field__label" for="link">Link to the project</label>
				<input class="input" id="link" name="link" type="url" placeholder="https://" value={project.link} />
				<p class="field__help">
					We need one for a project other people use. A beta or TestFlight link is fine. For anything else you can leave it empty.
				</p>
				<label class="check">
					<input type="checkbox" name="not_public" value="1" checked={project.notPublic} />
					<span>It isn’t public yet</span>
				</label>
			</div>
			<fieldset class="field">
				<legend class="field__label">Will the project make money?</legend>
				<div class="t-segmented portal-segmented">
					{COMMERCIAL.map((c) => (
						<label class="t-segmented__option">
							<input type="radio" name="commercial" value={c.slug} checked={project.commercial === c.slug} required />
							{c.label}
						</label>
					))}
				</div>
				<p class="field__help">
					Answer yes if it charges for anything, sells anything or shows adverts, or if a business uses it in its work. A donation link is fine. The <a href="/terms/">API terms</a> have examples.
				</p>
			</fieldset>
		</>
	);
}
