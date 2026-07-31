{{/*
rediensiam.hydraAdminUrl
Returns the Hydra admin API URL:
  - local.enabled=true  → internal cluster service derived from release name
  - local.enabled=false → rediensiam.hydra.external.adminUrl
*/}}
{{- define "rediensiam.hydraAdminUrl" -}}
{{- if .Values.rediensiam.hydra.local.enabled -}}
http://{{ .Release.Name }}-hydra-admin:4445
{{- else -}}
{{ .Values.rediensiam.hydra.external.adminUrl }}
{{- end -}}
{{- end -}}

{{/*
rediensiam.hydraPublicUrl
Returns the Hydra public API URL.
*/}}
{{- define "rediensiam.hydraPublicUrl" -}}
{{- if .Values.rediensiam.hydra.local.enabled -}}
http://{{ .Release.Name }}-hydra-public:4444
{{- else -}}
{{ .Values.rediensiam.hydra.external.publicUrl }}
{{- end -}}
{{- end -}}

{{/*
rediensiam.ketoReadUrl
Returns the Keto read API URL.
*/}}
{{- define "rediensiam.ketoReadUrl" -}}
{{- if .Values.rediensiam.keto.local.enabled -}}
http://{{ .Release.Name }}-keto-read:4466
{{- else -}}
{{ .Values.rediensiam.keto.external.readUrl }}
{{- end -}}
{{- end -}}

{{/*
rediensiam.ketoWriteUrl
Returns the Keto write API URL.
*/}}
{{- define "rediensiam.ketoWriteUrl" -}}
{{- if .Values.rediensiam.keto.local.enabled -}}
http://{{ .Release.Name }}-keto-write:4467
{{- else -}}
{{ .Values.rediensiam.keto.external.writeUrl }}
{{- end -}}
{{- end -}}

{{/*
rediensiam.dnsEgress
The one egress rule every pod in this release needs. Narrowed from "0.0.0.0/0 on :53" to the
kube-dns pods, because :53 to anywhere is the standard exfiltration channel.
If DNS breaks after an upgrade, this selector is the first thing to check: it assumes coredns
carries `k8s-app: kube-dns` in the namespace named by networkPolicy.dnsNamespace (k3s default).
*/}}
{{- define "rediensiam.dnsEgress" -}}
- ports:
    - {port: 53, protocol: UDP}
    - {port: 53, protocol: TCP}
  to:
    - namespaceSelector:
        matchLabels:
          kubernetes.io/metadata.name: {{ .Values.rediensiam.networkPolicy.dnsNamespace }}
      podSelector:
        matchLabels:
          k8s-app: kube-dns
{{- end -}}

{{/*
rediensiam.postgresPeer
The NetworkPolicy peer list for ":5432 to the database", for the app, Hydra and Keto egress
rules. With the built-in StatefulSet that is a pod label this chart owns. With an external
CloudNativePG cluster the chart owns nothing about those pods, so the selector has to be
supplied — `cnpg.io/cluster: <name>` is what CNPG stamps on its instances.

Getting this wrong is a silent outage in one direction only: too narrow and the app cannot
reach the database at all, which is loud. It is never fail-open, because the default-deny is
ingress-only and it is `postgres-lockdown` (built-in mode) or the CNPG cluster's own policy
(external mode) that decides who may connect.
*/}}
{{- define "rediensiam.postgresPeer" -}}
{{- if .Values.rediensiam.postgres.local.enabled -}}
- podSelector:
    matchLabels:
      app: {{ .Release.Name }}-postgres
{{- else -}}
- podSelector:
    matchLabels:
{{ toYaml .Values.rediensiam.postgres.external.podSelector | indent 6 }}
{{- if .Values.rediensiam.postgres.external.namespace }}
  namespaceSelector:
    matchLabels:
      kubernetes.io/metadata.name: {{ .Values.rediensiam.postgres.external.namespace }}
{{- end }}
{{- end -}}
{{- end -}}

{{/*
rediensiam.ingressPublicHost
Returns the public ingress hostname.
Uses rediensiam.ingress.public.host if set; otherwise parses host from rediensiam.publicUrl.
*/}}
{{- define "rediensiam.ingressPublicHost" -}}
{{- if .Values.rediensiam.ingress.public.host -}}
{{ .Values.rediensiam.ingress.public.host }}
{{- else -}}
{{ index (urlParse .Values.rediensiam.publicUrl) "host" }}
{{- end -}}
{{- end -}}

{{/*
rediensiam.ingressAdminHost
Returns the admin ingress hostname.
Uses rediensiam.ingress.admin.host if set; otherwise parses host from rediensiam.adminUrl.
*/}}
{{- define "rediensiam.ingressAdminHost" -}}
{{- if .Values.rediensiam.ingress.admin.host -}}
{{ .Values.rediensiam.ingress.admin.host }}
{{- else -}}
{{ index (urlParse .Values.rediensiam.adminUrl) "host" }}
{{- end -}}
{{- end -}}
