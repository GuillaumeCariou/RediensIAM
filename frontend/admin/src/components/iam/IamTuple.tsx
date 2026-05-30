interface IamTupleProps {
  ns: string;
  obj: string;
  rel: string;
  subj: string;
  className?: string;
}

export default function IamTuple({ ns, obj, rel, subj, className = '' }: Readonly<IamTupleProps>) {
  return (
    <span className={`iam-tuple ${className}`}>
      <span className="t-ns">{ns}</span>
      <span className="t-sep">:</span>
      <span className="t-obj">{obj}</span>
      <span className="t-sep">#</span>
      <span className="t-rel">{rel}</span>
      <span className="t-sep">@</span>
      <span className="t-subj">{subj}</span>
    </span>
  );
}
